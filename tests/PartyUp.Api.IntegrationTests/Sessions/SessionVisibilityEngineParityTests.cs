using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// PHASE5_DESIGN.md A-077 (b)/P5-VIS-01 (b): one shared fixture matrix driven through
/// <c>SessionVisibilitySql</c> at access-kind <c>any</c> (via <c>ListUpcomingAsync</c>) and through
/// <c>VisibilityEngine.ResolveSession</c> over the loaded aggregate, asserting the two admit the
/// identical session set. This is what binds the detail path — which stays in the Application engine
/// and is never migrated into SQL (A-077 (b)) — to the extracted fragment, without moving it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionVisibilityEngineParityTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task SqlFragmentAndVisibilityEngineAdmitTheSameSessions_D018_D019()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId viewerId = await _factory.SeedUserAsync(email: "parity-viewer@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: "parity-friend@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: "parity-stranger@example.test");
        UserId strangerTwoId = await _factory.SeedUserAsync(email: "parity-stranger-two@example.test");
        UserId blockedFriendId = await _factory.SeedUserAsync(email: "parity-blocked-friend@example.test");
        UserId blockedByFriendId = await _factory.SeedUserAsync(email: "parity-blocked-by-friend@example.test");
        UserId blockedExtenderId = await _factory.SeedUserAsync(email: "parity-blocked-extender@example.test");
        UserId extenderBlockingViewerId = await _factory.SeedUserAsync(email: "parity-extender-blocking-viewer@example.test");

        await LinkFriendsAsync(context, viewerId, friendId);
        await LinkFriendsAsync(context, viewerId, blockedFriendId);
        await BlockAsync(context, viewerId, blockedFriendId);
        // Rule 4 mirror: the organizer is the blocker, viewer the blocked party.
        await LinkFriendsAsync(context, viewerId, blockedByFriendId);
        await BlockAsync(context, blockedByFriendId, viewerId);
        // Extender sub-clause, both directions: the friend who would otherwise extend access is
        // blocked with the viewer, so their path is not counted (rule 4 inside rule 2's extender path).
        await LinkFriendsAsync(context, viewerId, blockedExtenderId);
        await BlockAsync(context, viewerId, blockedExtenderId);
        await LinkFriendsAsync(context, viewerId, extenderBlockingViewerId);
        await BlockAsync(context, extenderBlockingViewerId, viewerId);

        List<SessionId> sessionIds =
        [
            await CreateSessionAsync(context, viewerId, isPrivate: true, invitees: []), // organizer
            await CreateSessionAsync(context, strangerId, isPrivate: true, invitees: [viewerId], rsvpGoing: viewerId), // direct participant
            await CreateSessionAsync(context, friendId, isPrivate: false, invitees: [strangerId]), // inherited via organizer friend
            await CreateSessionAsync(context, strangerId, isPrivate: false, invitees: [friendId]), // inherited via extending participant friend
            await CreateSessionAsync(context, friendId, isPrivate: true, invitees: []), // private: rule 3 blocks the inherited path
            await CreateSessionAsync(context, blockedFriendId, isPrivate: false, invitees: [strangerId]), // rule 4: organizer blocked despite being a friend (viewer is blocker)
            await CreateSessionAsync(context, blockedByFriendId, isPrivate: false, invitees: [strangerId]), // rule 4 mirror: organizer is the blocker
            await CreateSessionAsync(context, strangerId, isPrivate: false, invitees: [strangerTwoId]), // unrelated
            await CreateSessionAsync(context, strangerId, isPrivate: true, invitees: [viewerId]), // invited, still Pending (no RSVP) — direct access via rule 1
            await CreateSessionAsync(context, strangerId, isPrivate: false, invitees: [blockedExtenderId], rsvpGoing: blockedExtenderId), // extender blocked by viewer — path not counted
            await CreateSessionAsync(context, strangerId, isPrivate: false, invitees: [extenderBlockingViewerId], rsvpGoing: extenderBlockingViewerId), // extender mirror: extender is the blocker
        ];

        Viewer viewer = await BuildViewerAsync(context, viewerId);

        HashSet<Guid> sqlAdmitted = (await repository.ListUpcomingAsync(viewerId, null, sessionIds.Count, default))
            .Items
            .Select(item => item.Id.Value)
            .ToHashSet();

        HashSet<Guid> engineAdmitted = [];
        foreach (SessionId sessionId in sessionIds)
        {
            Session session = await context.Sessions
                .AsNoTracking()
                .Include(s => s.Participations)
                .Include(s => s.JoinRequests)
                .SingleAsync(s => s.Id == sessionId);

            if (VisibilityEngine.ResolveSession(session, viewer).IsVisible)
            {
                engineAdmitted.Add(sessionId.Value);
            }
        }

        Assert.Equal(engineAdmitted, sqlAdmitted);
        // The matrix is only meaningful if it actually exercises both admission and exclusion.
        Assert.NotEmpty(engineAdmitted);
        Assert.True(engineAdmitted.Count < sessionIds.Count);
    }

    private static async Task<Viewer> BuildViewerAsync(AppDbContext context, UserId viewerId)
    {
        List<Friendship> friendships = await context.Friendships
            .AsNoTracking()
            .Where(f => f.UserLoId == viewerId || f.UserHiId == viewerId)
            .ToListAsync();
        List<Block> blocks = await context.Blocks
            .AsNoTracking()
            .Where(b => b.BlockerId == viewerId || b.BlockedId == viewerId)
            .ToListAsync();

        return Viewer.From(viewerId, friendships, blocks);
    }

    private async Task<SessionId> CreateSessionAsync(
        AppDbContext context,
        UserId organizerId,
        bool isPrivate,
        IReadOnlyCollection<UserId> invitees,
        UserId? rsvpGoing = null)
    {
        SessionDetails details = new(
            SessionTitle.Create("Parity fixture"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: isPrivate);

        Session session = Session.Create(organizerId, details, invitees, _factory.Clock);
        if (rsvpGoing is { } rsvpUserId)
        {
            session.Rsvp(ParticipantRef.ForUser(rsvpUserId), ParticipationStatus.Going, _factory.Clock);
        }

        _ = session.DequeueEvents();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task LinkFriendsAsync(AppDbContext context, UserId a, UserId b)
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

    private async Task BlockAsync(AppDbContext context, UserId blockerId, UserId blockedId)
    {
        User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
        Block block = Block.Create(blockerId, blockedId, blockedUser.Username!.Value, _factory.Clock);
        context.Blocks.Add(block);
        await context.SaveChangesAsync();
    }
}
