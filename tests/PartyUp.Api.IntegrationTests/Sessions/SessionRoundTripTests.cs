using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// S1's round-trip gate (PHASE4_DESIGN.md §4.3): save a Session with a full roster shape, clear the
/// context, reload it through <see cref="ISessionRepository.LoadForUpdateAsync"/>, and prove semantic
/// equality of status, roster, link state, requests, reminder choice, and the separately persisted
/// reminder handoff.
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
        UserId inviteeId = await SeedUserAsync(seedContext);
        UserId requesterId = await SeedUserAsync(seedContext);

        SessionDetails details = new(
            SessionTitle.Create("Round trip"),
            _clock.GetCurrentInstant() + Duration.FromHours(2),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Create("Bring snacks"),
            minAttendance: 1,
            maxCapacity: 10,
            isPrivate: true);

        Session session = Session.Create(organizerId, details, [inviteeId], _clock);
        session.Rsvp(ParticipantRef.ForUser(inviteeId), ParticipationStatus.Going, _clock);
        session.SetReminderChoice(inviteeId, ReminderOffset.Min30, _clock);

        (GuestLink link, string _) = session.CreateGuestLink(_clock);
        GuestIdentity identity = session.ClaimGuestLink(
            link.Id,
            DisplayName.Create("Guest One"),
            ParticipationStatus.Maybe,
            _clock);

        JoinRequest request = session.OpenJoinRequest(requesterId, _clock);

        SessionId sessionId = session.Id;
        GuestLinkId guestLinkId = link.Id;
        string tokenHashBeforeSave = link.TokenHash;

        var seedRepository = new SessionRepository(seedContext);
        seedRepository.Add(session);
        await seedContext.SaveChangesAsync();

        Instant dueAt = details.StartTime - Duration.FromMinutes(30);
        seedContext.SessionReminderHandoffs.Add(new SessionReminderHandoff
        {
            Id = Guid.CreateVersion7(),
            SessionId = sessionId,
            RecipientUserId = inviteeId,
            DueAt = dueAt,
            State = SessionReminderHandoffState.Pending,
            PlannedAt = _clock.GetCurrentInstant(),
            Version = 1,
            Attempts = 0,
        });
        await seedContext.SaveChangesAsync();

        await using AppDbContext reloadContext = CreateContext();
        var reloadRepository = new SessionRepository(reloadContext);
        Session? reloaded = await reloadRepository.LoadForUpdateAsync(sessionId, default);

        Assert.NotNull(reloaded);
        Assert.Equal(session.Status, reloaded!.Status);
        Assert.Equal(session.OrganizerId, reloaded.OrganizerId);
        Assert.Equal(session.Title, reloaded.Title);
        Assert.Equal(session.StartTime, reloaded.StartTime);

        Assert.Equal(3, reloaded.Participations.Count);
        Participation reloadedInvitee = Assert.Single(
            reloaded.Participations,
            p => p.UserId == inviteeId);
        Assert.Equal(ParticipationStatus.Going, reloadedInvitee.Status);
        Assert.False(reloadedInvitee.IsWaitlisted);
        Assert.Equal(ReminderOffset.Min30, reloadedInvitee.ReminderChoice);

        Participation reloadedGuest = Assert.Single(
            reloaded.Participations,
            p => p.GuestIdentityId == identity.Id);
        Assert.Equal(ParticipationStatus.Maybe, reloadedGuest.Status);

        GuestLink reloadedLink = Assert.Single(reloaded.GuestLinks, l => l.Id == guestLinkId);
        Assert.Equal(tokenHashBeforeSave, reloadedLink.TokenHash);
        Assert.True(reloadedLink.IsClaimed);
        Assert.Equal(identity.Id, reloadedLink.GuestIdentityId);
        Assert.Equal(GuestLinkState.Active, reloadedLink.State);

        JoinRequest reloadedRequest = Assert.Single(reloaded.JoinRequests, r => r.Id == request.Id);
        Assert.Equal(requesterId, reloadedRequest.RequesterId);
        Assert.True(reloadedRequest.IsPending);

        SessionReminderHandoff reloadedHandoff = await reloadContext.SessionReminderHandoffs
            .AsNoTracking()
            .SingleAsync(handoff => handoff.SessionId == sessionId);
        Assert.Equal(inviteeId, reloadedHandoff.RecipientUserId);
        Assert.Equal(SessionReminderHandoffState.Pending, reloadedHandoff.State);
        Assert.Equal(dueAt, reloadedHandoff.DueAt);
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
