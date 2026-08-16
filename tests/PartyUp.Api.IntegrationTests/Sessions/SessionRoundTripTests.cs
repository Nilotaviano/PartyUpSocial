using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// S1's round-trip gate (PHASE4_DESIGN.md §4.3): save a Session with the full shape inventory the
/// gate names, clear the context, reload it through <see cref="ISessionRepository.LoadForUpdateAsync"/>,
/// and prove semantic equality of status, roster, link state, requests, mutes, handoffs and reminder
/// choice. §4.3's inventory spans shapes that cannot coexist on one row (a game is one per session; a
/// <c>RevokedByCancel</c> link implies a canceled, terminal session) — those are covered by dedicated
/// facts below rather than forced into the primary scenario.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionRoundTripTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly MutableClock _clock = new(Instant.FromUtc(2026, 8, 4, 12, 0, 0));

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task LoadForUpdate_PreservesRosterLinksRequestsHandoffsAndStatus_D048()
    {
        await using AppDbContext seedContext = CreateContext();
        await seedContext.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(seedContext);
        UserId activeInviteeId = await SeedUserAsync(seedContext);
        UserId waitlistedInviteeId = await SeedUserAsync(seedContext);
        UserId requesterId = await SeedUserAsync(seedContext);
        UserId deliveredRecipientId = await SeedUserAsync(seedContext);
        UserId canceledRecipientId = await SeedUserAsync(seedContext);

        SessionDetails details = new(
            SessionTitle.Create("Round trip"),
            _clock.GetCurrentInstant() + Duration.FromHours(2),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Create("Bring snacks"),
            minAttendance: 1,
            maxCapacity: 3,
            visibility: ContentVisibility.Private);

        // Occupancy plan (max 3): organizer(1) -> activeInvitee Going(2) -> guestActive claim Going(3,
        // at capacity) -> waitlistedInvitee Going(waitlisted) -> guestWaitlisted claim Going(waitlisted).
        Session session = Session.Create(organizerId, details, [activeInviteeId, waitlistedInviteeId], _clock);
        session.Rsvp(ParticipantRef.ForUser(activeInviteeId), ParticipationStatus.Going, _clock);
        session.SetReminderChoice(activeInviteeId, ReminderOffset.Min30, _clock);

        (GuestLink activeLink, string _) = session.CreateGuestLink(_clock);
        GuestIdentity activeGuest = session.ClaimGuestLink(
            activeLink.Id,
            DisplayName.Create("Active Guest"),
            ParticipationStatus.Going,
            _clock);

        session.Rsvp(ParticipantRef.ForUser(waitlistedInviteeId), ParticipationStatus.Going, _clock);

        (GuestLink waitlistedLink, string _) = session.CreateGuestLink(_clock);
        GuestIdentity waitlistedGuest = session.ClaimGuestLink(
            waitlistedLink.Id,
            DisplayName.Create("Waitlisted Guest"),
            ParticipationStatus.Going,
            _clock);

        (GuestLink unclaimedLink, string _) = session.CreateGuestLink(_clock);

        JoinRequest request = session.OpenJoinRequest(requesterId, _clock);

        SessionId sessionId = session.Id;
        string activeLinkTokenHash = activeLink.TokenHash;
        string unclaimedLinkTokenHash = unclaimedLink.TokenHash;

        var seedRepository = new SessionRepository(seedContext);
        seedRepository.Add(session);
        await seedContext.SaveChangesAsync();

        NotificationMute mute = NotificationMute.ForSession(activeInviteeId, sessionId, _clock);
        seedContext.NotificationMutes.Add(mute);

        Instant dueAt = details.StartTime - Duration.FromMinutes(30);
        seedContext.SessionReminderHandoffs.Add(new SessionReminderHandoff
        {
            Id = Guid.CreateVersion7(),
            SessionId = sessionId,
            RecipientUserId = activeInviteeId,
            DueAt = dueAt,
            State = SessionReminderHandoffState.Pending,
            PlannedAt = _clock.GetCurrentInstant(),
            Version = 1,
            Attempts = 0,
        });
        seedContext.SessionReminderHandoffs.Add(new SessionReminderHandoff
        {
            Id = Guid.CreateVersion7(),
            SessionId = sessionId,
            RecipientUserId = deliveredRecipientId,
            DueAt = dueAt,
            State = SessionReminderHandoffState.Delivered,
            PlannedAt = _clock.GetCurrentInstant(),
            DeliveredAt = _clock.GetCurrentInstant(),
            Version = 1,
            Attempts = 1,
        });
        seedContext.SessionReminderHandoffs.Add(new SessionReminderHandoff
        {
            Id = Guid.CreateVersion7(),
            SessionId = sessionId,
            RecipientUserId = canceledRecipientId,
            DueAt = dueAt,
            State = SessionReminderHandoffState.Canceled,
            PlannedAt = _clock.GetCurrentInstant(),
            Version = 2,
            Attempts = 0,
        });
        await seedContext.SaveChangesAsync();

        await using AppDbContext reloadContext = CreateContext();
        var reloadRepository = new SessionRepository(reloadContext);
        SessionForUpdate? reloaded = await reloadRepository.LoadForUpdateAsync(sessionId, default);

        Assert.NotNull(reloaded);
        Session reloadedSession = reloaded!.Session;
        Assert.Equal(session.Status, reloadedSession.Status);
        Assert.Equal(session.OrganizerId, reloadedSession.OrganizerId);
        Assert.Equal(session.Title, reloadedSession.Title);
        Assert.Equal(session.StartTime, reloadedSession.StartTime);

        Assert.Equal(5, reloadedSession.Participations.Count);

        Participation reloadedActiveInvitee = Assert.Single(
            reloadedSession.Participations,
            p => p.UserId == activeInviteeId);
        Assert.Equal(ParticipationStatus.Going, reloadedActiveInvitee.Status);
        Assert.False(reloadedActiveInvitee.IsWaitlisted);
        Assert.Equal(ReminderOffset.Min30, reloadedActiveInvitee.ReminderChoice);

        Participation reloadedWaitlistedInvitee = Assert.Single(
            reloadedSession.Participations,
            p => p.UserId == waitlistedInviteeId);
        Assert.Equal(ParticipationStatus.Going, reloadedWaitlistedInvitee.Status);
        Assert.True(reloadedWaitlistedInvitee.IsWaitlisted);

        Participation reloadedActiveGuest = Assert.Single(
            reloadedSession.Participations,
            p => p.GuestIdentityId == activeGuest.Id);
        Assert.Equal(ParticipationStatus.Going, reloadedActiveGuest.Status);
        Assert.False(reloadedActiveGuest.IsWaitlisted);

        Participation reloadedWaitlistedGuest = Assert.Single(
            reloadedSession.Participations,
            p => p.GuestIdentityId == waitlistedGuest.Id);
        Assert.Equal(ParticipationStatus.Going, reloadedWaitlistedGuest.Status);
        Assert.True(reloadedWaitlistedGuest.IsWaitlisted);

        Assert.Equal(3, reloadedSession.GuestLinks.Count);

        GuestLink reloadedActiveLink = Assert.Single(reloadedSession.GuestLinks, l => l.Id == activeLink.Id);
        Assert.Equal(activeLinkTokenHash, reloadedActiveLink.TokenHash);
        Assert.True(reloadedActiveLink.IsClaimed);
        Assert.Equal(activeGuest.Id, reloadedActiveLink.GuestIdentityId);
        Assert.Equal(GuestLinkState.Active, reloadedActiveLink.State);

        GuestLink reloadedWaitlistedLink = Assert.Single(reloadedSession.GuestLinks, l => l.Id == waitlistedLink.Id);
        Assert.True(reloadedWaitlistedLink.IsClaimed);
        Assert.Equal(GuestLinkState.Active, reloadedWaitlistedLink.State);

        GuestLink reloadedUnclaimedLink = Assert.Single(reloadedSession.GuestLinks, l => l.Id == unclaimedLink.Id);
        Assert.Equal(unclaimedLinkTokenHash, reloadedUnclaimedLink.TokenHash);
        Assert.False(reloadedUnclaimedLink.IsClaimed);
        Assert.Equal(GuestLinkState.Active, reloadedUnclaimedLink.State);

        JoinRequest reloadedRequest = Assert.Single(reloadedSession.JoinRequests, r => r.Id == request.Id);
        Assert.Equal(requesterId, reloadedRequest.RequesterId);
        Assert.True(reloadedRequest.IsPending);

        NotificationMute reloadedMute = Assert.Single(reloaded.Mutes, m => m.Id == mute.Id);
        Assert.Equal(activeInviteeId, reloadedMute.UserId);
        Assert.True(reloadedMute.Targets(sessionId));
        Assert.True(reloadedMute.TargetsSession);

        Assert.Equal(3, reloaded.Handoffs.Count);

        SessionReminderHandoffLock reloadedPendingHandoff = Assert.Single(
            reloaded.Handoffs,
            h => h.RecipientUserId == activeInviteeId);
        Assert.Equal((int)SessionReminderHandoffState.Pending, reloadedPendingHandoff.State);
        Assert.Equal(dueAt, reloadedPendingHandoff.DueAt);

        SessionReminderHandoffLock reloadedDeliveredHandoff = Assert.Single(
            reloaded.Handoffs,
            h => h.RecipientUserId == deliveredRecipientId);
        Assert.Equal((int)SessionReminderHandoffState.Delivered, reloadedDeliveredHandoff.State);
        Assert.Equal(1, reloadedDeliveredHandoff.Attempts);

        SessionReminderHandoffLock reloadedCanceledHandoff = Assert.Single(
            reloaded.Handoffs,
            h => h.RecipientUserId == canceledRecipientId);
        Assert.Equal((int)SessionReminderHandoffState.Canceled, reloadedCanceledHandoff.State);
        Assert.Equal(2, reloadedCanceledHandoff.Version);
    }

    /// <summary>
    /// §4.3's remaining non-poll link states. <c>RevokedByRemoval</c> comes from removing a claimed
    /// guest before the session is touched further; <c>RevokedByCancel</c> comes from canceling the
    /// session afterwards, which revokes every link still <c>Active</c> at that point — first
    /// revocation wins (<see cref="GuestLink.Revoke"/>), so the already-removed link is untouched.
    /// </summary>
    [Fact]
    public async Task LoadForUpdate_PreservesRevokedGuestLinkStates_D048()
    {
        await using AppDbContext seedContext = CreateContext();
        await seedContext.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(seedContext);

        SessionDetails details = new(
            SessionTitle.Create("Revocation shapes"),
            _clock.GetCurrentInstant() + Duration.FromHours(2),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Private);

        Session session = Session.Create(organizerId, details, [], _clock);

        (GuestLink removalLink, string _) = session.CreateGuestLink(_clock);
        GuestIdentity removalGuest = session.ClaimGuestLink(
            removalLink.Id,
            DisplayName.Create("Removed Guest"),
            ParticipationStatus.Going,
            _clock);

        (GuestLink cancelLink, string _) = session.CreateGuestLink(_clock);

        session.RemoveGuest(removalGuest.Id, _clock);
        session.Cancel(_clock);

        SessionId sessionId = session.Id;

        var seedRepository = new SessionRepository(seedContext);
        seedRepository.Add(session);
        await seedContext.SaveChangesAsync();

        await using AppDbContext reloadContext = CreateContext();
        var reloadRepository = new SessionRepository(reloadContext);
        SessionForUpdate? reloaded = await reloadRepository.LoadForUpdateAsync(sessionId, default);

        Assert.NotNull(reloaded);
        Assert.Equal(SessionStatus.Canceled, reloaded!.Session.Status);

        GuestLink reloadedRemovalLink = Assert.Single(reloaded.Session.GuestLinks, l => l.Id == removalLink.Id);
        Assert.Equal(GuestLinkState.RevokedByRemoval, reloadedRemovalLink.State);

        GuestLink reloadedCancelLink = Assert.Single(reloaded.Session.GuestLinks, l => l.Id == cancelLink.Id);
        Assert.Equal(GuestLinkState.RevokedByCancel, reloadedCancelLink.State);
    }

    /// <summary>§4.3's game shapes: a Steam catalog reference and a free-text title on another platform.</summary>
    [Fact]
    public async Task LoadForUpdate_PreservesEveryGameShape_D048()
    {
        await using AppDbContext seedContext = CreateContext();
        await seedContext.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(seedContext);

        SessionGame steamGame = SessionGame.FromSteamCatalog(730, GameName.Create("Counter-Strike 2"));
        SessionDetails steamDetails = new(
            SessionTitle.Create("Steam catalog game"),
            _clock.GetCurrentInstant() + Duration.FromHours(2),
            Duration15.FromMinutes(60),
            Platform.Steam,
            steamGame,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Private);
        Session steamSession = Session.Create(organizerId, steamDetails, [], _clock);

        SessionGame freeTextGame = SessionGame.FreeText(GameName.Create("Rocket League"));
        SessionDetails freeTextDetails = new(
            SessionTitle.Create("Free text game"),
            _clock.GetCurrentInstant() + Duration.FromHours(2),
            Duration15.FromMinutes(60),
            Platform.PlayStation,
            freeTextGame,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Private);
        Session freeTextSession = Session.Create(organizerId, freeTextDetails, [], _clock);

        var seedRepository = new SessionRepository(seedContext);
        seedRepository.Add(steamSession);
        seedRepository.Add(freeTextSession);
        await seedContext.SaveChangesAsync();

        await using AppDbContext reloadContext = CreateContext();
        var reloadRepository = new SessionRepository(reloadContext);

        SessionForUpdate? reloadedSteam = await reloadRepository.LoadForUpdateAsync(steamSession.Id, default);
        Assert.NotNull(reloadedSteam);
        Assert.Equal(Platform.Steam, reloadedSteam!.Session.Platform);
        Assert.NotNull(reloadedSteam.Session.Game);
        Assert.True(reloadedSteam.Session.Game!.IsCatalogReference);
        Assert.Equal(730, reloadedSteam.Session.Game.SteamAppId);
        Assert.Equal(steamGame.Name, reloadedSteam.Session.Game.Name);

        SessionForUpdate? reloadedFreeText = await reloadRepository.LoadForUpdateAsync(freeTextSession.Id, default);
        Assert.NotNull(reloadedFreeText);
        Assert.Equal(Platform.PlayStation, reloadedFreeText!.Session.Platform);
        Assert.NotNull(reloadedFreeText.Session.Game);
        Assert.False(reloadedFreeText.Session.Game!.IsCatalogReference);
        Assert.Null(reloadedFreeText.Session.Game.SteamAppId);
        Assert.Equal(freeTextGame.Name, reloadedFreeText.Session.Game.Name);
    }

    private static async Task<UserId> SeedUserAsync(AppDbContext context)
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create($"PU-{Guid.NewGuid():N}"[..9]),
            AgeGateSource.SelfAttested,
            SystemClock.Instance);
        context.DomainUsers.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
