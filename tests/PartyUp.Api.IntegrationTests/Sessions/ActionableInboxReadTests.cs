using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// PHASE5_DESIGN.md §5.1/P5-HOME-01: <c>ISessionReadRepository.ListActionableAsync</c> — D-103's Home
/// region 1. Pending invitations awaiting the viewer's response and join requests awaiting the
/// viewer's organizer decision, both future-and-Unconfirmed-or-Confirmed only, through the A-077
/// fragment.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ActionableInboxReadTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task PendingInvitationsAndOrganizerJoinRequestsAreReturnedAndOrderedByStart_D103()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId viewerId = await _factory.SeedUserAsync(email: "inbox-viewer@example.test");
        UserId inviterOneId = await _factory.SeedUserAsync(email: "inbox-inviter-1@example.test");
        UserId inviterTwoId = await _factory.SeedUserAsync(email: "inbox-inviter-2@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: "inbox-requester@example.test");
        await MakeAcceptedFriendsAsync(context, viewerId, requesterId);

        Instant now = _factory.Clock.GetCurrentInstant();

        // Two invitations, deliberately created out of start-time order.
        SessionId laterInvite = await CreateSessionAsync(context, inviterTwoId, now + Duration.FromHours(5), [viewerId]);
        SessionId earlierInvite = await CreateSessionAsync(context, inviterOneId, now + Duration.FromHours(2), [viewerId]);

        // The viewer organizes their own session, and a friend opens a Pending join request on it.
        SessionId ownSession = await CreateSessionAsync(context, viewerId, now + Duration.FromHours(3), [requesterId]);
        await RsvpGoingAsync(context, ownSession, requesterId);
        SessionId requestTarget = await CreateSessionAsync(context, viewerId, now + Duration.FromHours(4), []);
        await OpenJoinRequestAsync(context, requestTarget, requesterId);

        ActionableInbox inbox = await reads.ListActionableAsync(viewerId, now, 50, default);

        Assert.Equal(2, inbox.Invitations.Count);
        Assert.Equal(earlierInvite, inbox.Invitations[0].Session.Id);
        Assert.Equal(laterInvite, inbox.Invitations[1].Session.Id);
        Assert.All(inbox.Invitations, i => Assert.Equal(ViewerRelation.Invitee, i.Session.ViewerRelation));

        JoinRequestId joinRequestId = Assert.Single(inbox.JoinRequests, r => r.Session.Id == requestTarget).Id;
        Assert.NotEqual(default, joinRequestId.Value);
        ActionableJoinRequest joinRequestRow = inbox.JoinRequests.Single(r => r.Id == joinRequestId);
        Assert.Equal(ViewerRelation.Organizer, joinRequestRow.Session.ViewerRelation);
        Assert.NotNull(joinRequestRow.RequesterDisplayName);
        Assert.False(inbox.HasMoreInvitations);
        Assert.False(inbox.HasMoreJoinRequests);
    }

    [Fact]
    public async Task AGoingRsvpIsNotAnInvitationAndViewerRelationDistinguishesThem_D103()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId viewerId = await _factory.SeedUserAsync(email: "inbox-going-viewer@example.test");
        UserId organizerId = await _factory.SeedUserAsync(email: "inbox-going-organizer@example.test");
        Instant now = _factory.Clock.GetCurrentInstant();

        SessionId acceptedSession = await CreateSessionAsync(context, organizerId, now + Duration.FromHours(2), [viewerId]);
        await RsvpGoingAsync(context, acceptedSession, viewerId);

        SessionId stillPendingSession = await CreateSessionAsync(context, organizerId, now + Duration.FromHours(3), [viewerId]);

        ActionableInbox inbox = await reads.ListActionableAsync(viewerId, now, 50, default);

        Assert.DoesNotContain(inbox.Invitations, i => i.Session.Id == acceptedSession);
        Assert.Contains(inbox.Invitations, i => i.Session.Id == stillPendingSession);
    }

    [Fact]
    public async Task PastCanceledAndExpiredSessionsAreExcluded_D103()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId viewerId = await _factory.SeedUserAsync(email: "inbox-past-viewer@example.test");
        UserId organizerId = await _factory.SeedUserAsync(email: "inbox-past-organizer@example.test");
        UserId expiredOrganizerId = await _factory.SeedUserAsync(email: "inbox-past-expired-organizer@example.test");
        UserId pastStartOrganizerId = await _factory.SeedUserAsync(email: "inbox-past-start-organizer@example.test");
        Instant now = _factory.Clock.GetCurrentInstant();

        SessionId canceledSession = await CreateSessionAsync(context, organizerId, now + Duration.FromHours(2), [viewerId]);
        await CancelAsync(context, canceledSession);

        // Expired: materialized status set directly at the database boundary (A-067 — the predicate
        // reads the column only, never re-derives it), per the same idiom
        // SessionVisibilitySqlCharacterizationTests uses for Completed.
        SessionId expiredSession = await CreateSessionAsync(context, expiredOrganizerId, now + Duration.FromHours(2), [viewerId]);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sessions SET status = 3 WHERE id = {expiredSession.Value}");

        // start_time > now: a session created with a valid future start, then moved into the past at
        // the database boundary (Session.Create forbids constructing one directly) — proves the
        // future-only filter is actually exercised, not just "yesterday's invitation never existed".
        SessionId pastStartSession = await CreateSessionAsync(
            context, pastStartOrganizerId, now + Duration.FromHours(2), [viewerId]);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sessions SET start_time = {now - Duration.FromHours(1)} WHERE id = {pastStartSession.Value}");

        SessionId futureSession = await CreateSessionAsync(context, organizerId, now + Duration.FromHours(2), [viewerId]);

        ActionableInbox inbox = await reads.ListActionableAsync(viewerId, now, 50, default);

        Assert.DoesNotContain(inbox.Invitations, i => i.Session.Id == canceledSession);
        Assert.DoesNotContain(inbox.Invitations, i => i.Session.Id == expiredSession);
        Assert.DoesNotContain(inbox.Invitations, i => i.Session.Id == pastStartSession);
        Assert.Contains(inbox.Invitations, i => i.Session.Id == futureSession);
    }

    [Fact]
    public async Task InvisibleSessionsNeverAppear_D019()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId viewerId = await _factory.SeedUserAsync(email: "inbox-invisible-viewer@example.test");
        UserId organizerId = await _factory.SeedUserAsync(email: "inbox-invisible-organizer@example.test");
        UserId organizerTwoId = await _factory.SeedUserAsync(email: "inbox-invisible-organizer-2@example.test");
        Instant now = _factory.Clock.GetCurrentInstant();

        // A block between the viewer and organizer removes even an active invitation from the inbox —
        // the A-077 fragment applies here too, so a session the viewer cannot see never appears. Viewer
        // is the blocker here.
        SessionId invitedThenBlocked = await CreateSessionAsync(context, organizerId, now + Duration.FromHours(2), [viewerId]);
        await BlockAsync(context, viewerId, organizerId);

        // Mirror direction: the organizer is the blocker — a test whose green survives deleting one
        // arm of SessionVisibilitySql's block predicate would not close this.
        SessionId invitedThenBlockedByOrganizer = await CreateSessionAsync(
            context, organizerTwoId, now + Duration.FromHours(2), [viewerId]);
        await BlockAsync(context, organizerTwoId, viewerId);

        ActionableInbox inbox = await reads.ListActionableAsync(viewerId, now, 50, default);

        Assert.DoesNotContain(inbox.Invitations, i => i.Session.Id == invitedThenBlocked);
        Assert.DoesNotContain(inbox.Invitations, i => i.Session.Id == invitedThenBlockedByOrganizer);
    }

    private async Task<SessionId> CreateSessionAsync(
        AppDbContext context, UserId organizerId, Instant startTime, IReadOnlyCollection<UserId> invitees)
    {
        SessionDetails details = new(
            SessionTitle.Create("Inbox fixture"),
            startTime,
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: (invitees.Count == 0 ? PartyUp.Domain.Kernel.ContentVisibility.Private : PartyUp.Domain.Kernel.ContentVisibility.Public));

        Session session = Session.Create(organizerId, details, invitees, _factory.Clock);
        _ = session.DequeueEvents();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task RsvpGoingAsync(AppDbContext context, SessionId sessionId, UserId userId)
    {
        Session session = await context.Sessions.Include(s => s.Participations).SingleAsync(s => s.Id == sessionId);
        session.Rsvp(ParticipantRef.ForUser(userId), ParticipationStatus.Going, _factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task OpenJoinRequestAsync(AppDbContext context, SessionId sessionId, UserId requesterId)
    {
        Session session = await context.Sessions
            .Include(s => s.Participations)
            .Include(s => s.JoinRequests)
            .SingleAsync(s => s.Id == sessionId);
        session.OpenJoinRequest(requesterId, _factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task CancelAsync(AppDbContext context, SessionId sessionId)
    {
        Session session = await context.Sessions
            .Include(s => s.Participations)
            .Include(s => s.JoinRequests)
            .Include(s => s.GuestLinks)
            .SingleAsync(s => s.Id == sessionId);
        session.Cancel(_factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task BlockAsync(AppDbContext context, UserId blockerId, UserId blockedId)
    {
        User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
        Block block = Block.Create(blockerId, blockedId, blockedUser.Username!.Value, _factory.Clock);
        context.Blocks.Add(block);
        await context.SaveChangesAsync();
    }

    private async Task MakeAcceptedFriendsAsync(AppDbContext context, UserId a, UserId b)
    {
        Friendship friendship = Friendship.Request(
            await context.DomainUsers.SingleAsync(u => u.Id == a),
            await context.DomainUsers.SingleAsync(u => u.Id == b),
            _factory.Clock);
        friendship.Accept(b, _factory.Clock);
        _ = friendship.DequeueEvents();
        context.Friendships.Add(friendship);
        await context.SaveChangesAsync();
    }
}
