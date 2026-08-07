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
/// PHASE5_DESIGN.md A-077 (c)/(d)/(f), P5-VIS-01 (c): <c>ISessionReadRepository.ListDiscoverAsync</c> —
/// the <c>inheritedOnly</c> access-kind plus the already-attached exclusion set. Proven here as a
/// two-sided matrix: what Discover returns and what it never does, and separately, D-149's masking
/// convention on the rows it does return.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DiscoverReadTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task InheritedOnlyAndNeverAlreadyAttached_D149_D018()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId viewerId = await _factory.SeedUserAsync(email: "disc-viewer@example.test");
        UserId friendOrganizerId = await _factory.SeedUserAsync(email: "disc-friend-organizer@example.test");
        UserId strangerOrganizerId = await _factory.SeedUserAsync(email: "disc-stranger-organizer@example.test");
        UserId extenderFriendId = await _factory.SeedUserAsync(email: "disc-extender@example.test");

        await MakeAcceptedFriendsAsync(context, viewerId, friendOrganizerId);
        await MakeAcceptedFriendsAsync(context, viewerId, extenderFriendId);

        // (a) friend-organized public session, viewer unattached — discoverable via friendship with organizer.
        SessionId viaOrganizerFriend = await CreateSessionAsync(context, friendOrganizerId, isPrivate: false, invitees: []);

        // (b) stranger-organized public session with an extending friend Going — discoverable via the extender.
        SessionId viaExtender = await CreateSessionAsync(
            context, strangerOrganizerId, isPrivate: false, invitees: [extenderFriendId]);
        await RsvpGoingAsync(context, viaExtender, extenderFriendId);

        // (c) viewer already organizes their own friend-visible-adjacent session — excluded regardless of anything else.
        SessionId ownSession = await CreateSessionAsync(context, viewerId, isPrivate: false, invitees: [extenderFriendId]);

        // (d) viewer already invited (Pending) to a friend-organized session — excluded.
        SessionId alreadyInvited = await CreateSessionAsync(
            context, friendOrganizerId, isPrivate: false, invitees: [viewerId]);

        // (e) viewer has a Pending join request on a friend-organized session — excluded.
        SessionId viewerRequested = await CreateSessionAsync(context, friendOrganizerId, isPrivate: false, invitees: [extenderFriendId]);
        await OpenJoinRequestAsync(context, viewerRequested, viewerId);

        // (f) private session organized by a friend — rule 3 excludes it even though inherited access would otherwise apply.
        SessionId privateFriend = await CreateSessionAsync(context, friendOrganizerId, isPrivate: true, invitees: []);

        // (g) no relation at all: stranger organizer, no friend extender — excluded.
        SessionId unrelated = await CreateSessionAsync(context, strangerOrganizerId, isPrivate: false, invitees: []);

        SessionPage<SessionSummary> page = await reads.ListDiscoverAsync(viewerId, null, 20, default);
        HashSet<Guid> admitted = [.. page.Items.Select(s => s.Id.Value)];

        Assert.Contains(viaOrganizerFriend.Value, admitted);
        Assert.Contains(viaExtender.Value, admitted);
        Assert.DoesNotContain(ownSession.Value, admitted);
        Assert.DoesNotContain(alreadyInvited.Value, admitted);
        Assert.DoesNotContain(viewerRequested.Value, admitted);
        Assert.DoesNotContain(privateFriend.Value, admitted);
        Assert.DoesNotContain(unrelated.Value, admitted);

        Assert.All(page.Items, s => Assert.Equal(ViewerRelation.Inherited, s.ViewerRelation));
    }

    [Fact]
    public async Task OrganizerCarriesNoUserIdAndATombstonedNameIsNull_D149()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId viewerId = await _factory.SeedUserAsync(email: "disc-mask-viewer@example.test");
        UserId organizerId = await _factory.SeedUserAsync(email: "disc-mask-organizer@example.test");
        await MakeAcceptedFriendsAsync(context, viewerId, organizerId);

        SessionId sessionId = await CreateSessionAsync(context, organizerId, isPrivate: false, invitees: []);

        User organizer = await context.DomainUsers.SingleAsync(u => u.Id == organizerId);
        organizer.RequestDeletion(_factory.Clock);
        _factory.Clock.Advance(Duration.FromDays(8));
        organizer.MarkDeleted(_factory.Clock);
        await context.SaveChangesAsync();

        SessionPage<SessionSummary> page = await reads.ListDiscoverAsync(viewerId, null, 20, default);
        SessionSummary row = Assert.Single(page.Items, s => s.Id == sessionId);

        // Ordinal/id-omission on the wire is S5's contract mapping; the read model's half of the
        // convention (§5.1) is that a tombstoned organizer's name resolves to null here.
        Assert.Null(row.OrganizerDisplayName);
        Assert.Equal(organizerId, row.OrganizerId);
    }

    [Fact]
    public async Task PrivateBlockedLapsedAndAlreadyAttachedReturnNothing_D047_D065()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId viewerId = await _factory.SeedUserAsync(email: "disc-neg-viewer@example.test");
        UserId blockedOrganizerId = await _factory.SeedUserAsync(email: "disc-neg-blocked-org@example.test");
        UserId lapsedOrganizerId = await _factory.SeedUserAsync(email: "disc-neg-lapsed-org@example.test");
        UserId requesterFriendId = await _factory.SeedUserAsync(email: "disc-neg-friend@example.test");

        await MakeAcceptedFriendsAsync(context, viewerId, blockedOrganizerId);
        await BlockAsync(context, viewerId, blockedOrganizerId);

        // rule 4: organizer blocked despite being (formerly) a friend.
        SessionId blockedOrganizerSession = await CreateSessionAsync(context, blockedOrganizerId, isPrivate: false, invitees: []);

        // withdrawn join request: no longer Pending, so it grants nothing.
        await MakeAcceptedFriendsAsync(context, viewerId, lapsedOrganizerId);
        SessionId withdrawnRequestSession = await CreateSessionAsync(context, lapsedOrganizerId, isPrivate: false, invitees: []);
        await OpenJoinRequestAsync(context, withdrawnRequestSession, viewerId);
        await WithdrawJoinRequestAsync(context, withdrawnRequestSession, viewerId);

        // Canceled status: excluded regardless of inherited access.
        UserId canceledOrganizerId = await _factory.SeedUserAsync(email: "disc-neg-canceled-org@example.test");
        await MakeAcceptedFriendsAsync(context, viewerId, canceledOrganizerId);
        SessionId canceledSession = await CreateSessionAsync(context, canceledOrganizerId, isPrivate: false, invitees: []);
        await CancelAsync(context, canceledSession);

        SessionPage<SessionSummary> page = await reads.ListDiscoverAsync(viewerId, null, 20, default);
        HashSet<Guid> admitted = [.. page.Items.Select(s => s.Id.Value)];

        Assert.DoesNotContain(blockedOrganizerSession.Value, admitted);
        // The withdrawn request no longer blocks Discover — the friendship with the organizer still
        // admits it, so this proves the exclusion is Pending-scoped, not "ever requested".
        Assert.Contains(withdrawnRequestSession.Value, admitted);
        Assert.DoesNotContain(canceledSession.Value, admitted);
        Assert.DoesNotContain(requesterFriendId.Value, admitted); // never a session id — sanity guard against a malformed fixture.
    }

    [Fact]
    public async Task RemovedParticipationIsNotDiscoverableBecauseRemovalIsPermanent_D018_D149()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId viewerId = await _factory.SeedUserAsync(email: "disc-removed-viewer@example.test");
        UserId organizerId = await _factory.SeedUserAsync(email: "disc-removed-organizer@example.test");
        UserId extenderId = await _factory.SeedUserAsync(email: "disc-removed-extender@example.test");
        await MakeAcceptedFriendsAsync(context, viewerId, extenderId);

        // A-077 (d): a Removed participation disqualifies the session from Discover because removal is
        // permanent (DOMAIN_MODEL.md:93) — even though the viewer has an otherwise-unblocked inherited
        // path through the extending friend, which would admit an unattached viewer.
        SessionId sessionId = await CreateSessionAsync(
            context, organizerId, isPrivate: false, invitees: [viewerId, extenderId]);
        await RsvpGoingAsync(context, sessionId, extenderId);
        await RemoveParticipationAsync(context, sessionId, viewerId);

        SessionPage<SessionSummary> page = await reads.ListDiscoverAsync(viewerId, null, 20, default);
        Assert.DoesNotContain(page.Items, s => s.Id == sessionId);
    }

    private async Task RemoveParticipationAsync(AppDbContext context, SessionId sessionId, UserId userId)
    {
        Session session = await context.Sessions.Include(s => s.Participations).SingleAsync(s => s.Id == sessionId);
        session.RemoveParticipation(ParticipantRef.ForUser(userId), _factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task<SessionId> CreateSessionAsync(
        AppDbContext context, UserId organizerId, bool isPrivate, IReadOnlyCollection<UserId> invitees)
    {
        SessionDetails details = new(
            SessionTitle.Create("Discover fixture"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: isPrivate);

        // D-099: a non-private session needs at least one directly invited registered friend at
        // creation — fixtures that want an empty roster otherwise draw in an unrelated filler invitee.
        List<UserId> effectiveInvitees = [.. invitees];
        if (!isPrivate && effectiveInvitees.Count == 0)
        {
            effectiveInvitees.Add(await _factory.SeedUserAsync(email: $"disc-filler-{Guid.NewGuid():N}@example.test"));
        }

        Session session = Session.Create(organizerId, details, effectiveInvitees, _factory.Clock);
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

    private async Task WithdrawJoinRequestAsync(AppDbContext context, SessionId sessionId, UserId requesterId)
    {
        Session session = await context.Sessions
            .Include(s => s.Participations)
            .Include(s => s.JoinRequests)
            .SingleAsync(s => s.Id == sessionId);
        JoinRequest request = session.JoinRequests.Single(r => r.RequesterId == requesterId);
        session.WithdrawJoinRequest(request.Id, _factory.Clock);
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
