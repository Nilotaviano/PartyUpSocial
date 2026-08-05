using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Application.Tests.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SessionCommandTransactionTests
{
    [Fact]
    public async Task Create_RollsBackSessionNotificationAndHandoff_D005_D112_D082()
    {
        SessionTestFixture fixture = new();
        var throwingMaterializer = new ThrowingSessionNotificationMaterializer();
        CreateSessionUseCase useCase = fixture.CreateUseCase(materializer: throwingMaterializer);

        CreateSessionCommand command = fixture.CreateCommand();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(command, default));

        // The aggregate write, the notification materialization, and the handoff replan all sat
        // inside one UnitOfWork.ExecuteAsync; the thrown failure rolled all three back together.
        Assert.Empty(fixture.Sessions.All);
        Assert.Empty(fixture.Reminders.Replans);
        Assert.Equal(1, fixture.UnitOfWork.Executions);
    }
}

/// <summary>Throws after being handed events, simulating a failed notification write mid-transaction.</summary>
internal sealed class ThrowingSessionNotificationMaterializer : ISessionNotificationMaterializer
{
    public Task MaterializeAsync(
        Session session,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Simulated notification-write failure.");
}

// ---- shared in-memory test doubles for every Sessions application-layer test file -----------------

internal sealed class FakeSessionRepository : ISessionRepository
{
    private Dictionary<SessionId, Session> _sessions = [];

    internal IReadOnlyCollection<Session> All => _sessions.Values;

    internal List<SessionId> LoadForUpdateCalls { get; } = [];

    public void Add(Session session) => _sessions[session.Id] = session;

    public Task<Session?> FindAsync(SessionId id, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.TryGetValue(id, out Session? session) ? session : null);

    public Task<SessionForUpdate?> LoadForUpdateAsync(SessionId id, CancellationToken cancellationToken)
    {
        LoadForUpdateCalls.Add(id);
        return Task.FromResult(_sessions.TryGetValue(id, out Session? session)
            ? new SessionForUpdate(session, [], [])
            : null);
    }

    internal Dictionary<SessionId, Session> Snapshot() => new(_sessions);

    internal void Restore(Dictionary<SessionId, Session> snapshot) => _sessions = new(snapshot);
}

internal sealed class FakeSessionLockRepository : ISessionLockRepository
{
    internal List<IReadOnlyCollection<UserId>> LockedUserBatches { get; } = [];

    internal List<IReadOnlyCollection<SessionId>> LockedSessionBatches { get; } = [];

    public Task LockUsersAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken)
    {
        LockedUserBatches.Add(userIds);
        return Task.CompletedTask;
    }

    public Task LockSessionsAsync(IReadOnlyCollection<SessionId> sessionIds, CancellationToken cancellationToken)
    {
        LockedSessionBatches.Add(sessionIds);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingSessionNotificationMaterializer : ISessionNotificationMaterializer
{
    internal List<(Session Session, IReadOnlyList<IDomainEvent> Events)> Calls { get; } = [];

    public Task MaterializeAsync(
        Session session,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken)
    {
        Calls.Add((session, events));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingReminderHandoffWriter : ISessionReminderHandoffWriter
{
    internal List<(SessionId SessionId, UserId UserId)> Replans { get; } = [];

    internal List<(SessionId SessionId, UserId UserId)> Cancels { get; } = [];

    public Task ReplanAsync(Session session, UserId recipientUserId, CancellationToken cancellationToken)
    {
        Replans.Add((session.Id, recipientUserId));
        return Task.CompletedTask;
    }

    public Task CancelAsync(SessionId sessionId, UserId recipientUserId, CancellationToken cancellationToken)
    {
        Cancels.Add((sessionId, recipientUserId));
        return Task.CompletedTask;
    }
}

internal sealed class FakeFriendshipRepository : IFriendshipRepository
{
    internal List<Friendship> Items { get; } = [];

    public Task<Friendship?> FindPairAsync(UserId first, UserId second, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(f => f.IsBetween(first, second)));

    public Task<Friendship?> FindPairForUpdateAsync(UserId first, UserId second, CancellationToken cancellationToken) =>
        FindPairAsync(first, second, cancellationToken);

    public Task<FriendshipPair?> FindOwnedPairAsync(
        FriendshipId friendshipId, UserId actorId, CancellationToken cancellationToken) =>
        Task.FromResult<FriendshipPair?>(null);

    public Task<Friendship?> FindByIdForUpdateAsync(FriendshipId friendshipId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(f => f.Id == friendshipId));

    public Task<IReadOnlyList<AcceptedFriendProjection>> ListAcceptedAsync(
        UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AcceptedFriendProjection>>([]);

    public Task<IReadOnlyList<PendingFriendProjection>> ListPendingAsync(
        UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingFriendProjection>>([]);

    public void Add(Friendship friendship) => Items.Add(friendship);

    public void Remove(Friendship friendship) => Items.Remove(friendship);

    /// <summary>Builds two fresh Active users, an Accepted friendship between them, and returns both ids.</summary>
    internal (UserId First, UserId Second) MakeAccepted(IClock clock)
    {
        User a = FriendTestData.Active($"u{Guid.NewGuid():N}"[..12], "A");
        User b = FriendTestData.Active($"u{Guid.NewGuid():N}"[..12], "B");
        Friendship friendship = Friendship.Request(a, b, clock);
        friendship.Accept(b.Id, clock);
        _ = friendship.DequeueEvents();
        Items.Add(friendship);
        return (a.Id, b.Id);
    }
}

internal sealed class FakeBlockRepository : IBlockRepository
{
    internal List<Block> Items { get; } = [];

    public Task<IReadOnlyList<Block>> FindPairForUpdateAsync(
        UserId first, UserId second, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Block>>(
            [.. Items.Where(b =>
                (b.BlockerId == first && b.BlockedId == second) ||
                (b.BlockerId == second && b.BlockedId == first))]);

    public Task<BlockPair?> FindOutboundOwnedPairAsync(
        BlockId blockId, UserId blockerId, CancellationToken cancellationToken) =>
        Task.FromResult<BlockPair?>(null);

    public Task<Block?> FindByIdForUpdateAsync(BlockId blockId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(b => b.Id == blockId));

    public Task<IReadOnlyList<BlockedUserProjection>> ListOutboundAsync(
        UserId blockerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BlockedUserProjection>>([]);

    public void Add(Block block) => Items.Add(block);

    public void Remove(Block block) => Items.Remove(block);
}

internal sealed class RollbackAwareUnitOfWork(FakeSessionRepository sessions) : IUnitOfWork
{
    internal int Executions { get; private set; }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        Executions++;
        Dictionary<SessionId, Session> snapshot = sessions.Snapshot();
        try
        {
            return await work(cancellationToken);
        }
        catch
        {
            sessions.Restore(snapshot);
            throw;
        }
    }
}

internal sealed class MutableClock(Instant current) : IClock
{
    public Instant GetCurrentInstant() => current;

    public void Advance(Duration duration) => current += duration;
}

/// <summary>
/// Shared fixture wiring every Sessions application-layer fake together with a fixed, replaceable
/// clock and one organizer + invitee pair whose friendship is Accepted, matching D-099/invite
/// eligibility out of the box.
/// </summary>
internal sealed class SessionTestFixture
{
    internal SessionTestFixture()
    {
        Clock = new MutableClock(Origin);
        Sessions = new FakeSessionRepository();
        Locks = new FakeSessionLockRepository();
        Friendships = new FakeFriendshipRepository();
        Blocks = new FakeBlockRepository();
        Materializer = new RecordingSessionNotificationMaterializer();
        Reminders = new RecordingReminderHandoffWriter();
        UnitOfWork = new RollbackAwareUnitOfWork(Sessions);
        Reads = new FakeSessionReadRepository(Sessions);

        (OrganizerId, InviteeId) = Friendships.MakeAccepted(Clock);
    }

    internal static Instant Origin { get; } = Instant.FromUtc(2026, 3, 2, 12, 0);

    internal static Instant DefaultStart => Origin + Duration.FromHours(24);

    internal MutableClock Clock { get; }

    internal FakeSessionRepository Sessions { get; }

    internal FakeSessionLockRepository Locks { get; }

    internal FakeFriendshipRepository Friendships { get; }

    internal FakeBlockRepository Blocks { get; }

    internal RecordingSessionNotificationMaterializer Materializer { get; }

    internal RecordingReminderHandoffWriter Reminders { get; }

    internal RollbackAwareUnitOfWork UnitOfWork { get; }

    internal FakeSessionReadRepository Reads { get; }

    internal UserId OrganizerId { get; }

    internal UserId InviteeId { get; }

    internal CreateSessionCommand CreateCommand(
        string title = "Raid night",
        Instant? startTime = null,
        int? durationMinutes = null,
        Platform? platform = null,
        int? minAttendance = null,
        int? maxCapacity = null,
        bool? isPrivate = null,
        IReadOnlyList<UserId>? inviteeIds = null,
        int? guestLinkCount = null) =>
        new(
            OrganizerId,
            title,
            startTime ?? DefaultStart,
            durationMinutes,
            platform,
            null,
            null,
            minAttendance,
            maxCapacity,
            isPrivate,
            inviteeIds ?? [InviteeId],
            guestLinkCount);

    internal CreateSessionUseCase CreateUseCase(ISessionNotificationMaterializer? materializer = null) =>
        new(
            Sessions,
            Locks,
            Friendships,
            Blocks,
            materializer ?? Materializer,
            Reminders,
            UnitOfWork,
            Clock);

    internal EditSessionUseCase EditUseCase() =>
        new(Sessions, Locks, Materializer, Reminders, UnitOfWork, Clock);

    internal SessionInvitationUseCases InvitationUseCases() =>
        new(Sessions, Locks, Friendships, Blocks, Materializer, Reminders, UnitOfWork, Clock);

    internal SessionParticipationUseCases ParticipationUseCases() =>
        new(Sessions, Locks, Materializer, Reminders, UnitOfWork, Clock);

    internal SessionJoinRequestUseCases JoinRequestUseCases() =>
        new(Sessions, Reads, Locks, Materializer, Reminders, UnitOfWork, Clock);

    internal SetSessionReminderUseCase ReminderUseCase() =>
        new(Sessions, Locks, Reminders, UnitOfWork, Clock);

    internal SessionReadUseCases ReadUseCases() =>
        new(Sessions, Reads, Friendships, Blocks);

    /// <summary>Creates and returns a session directly on the repository, bypassing the use case.</summary>
    internal Session SeedSession(
        int inviteeCount = 1,
        int minAttendance = 1,
        int? maxCapacity = null,
        bool isPrivate = false,
        Instant? startTime = null,
        int durationMinutes = 120,
        IReadOnlyList<UserId>? invitees = null,
        UserId? organizerId = null)
    {
        List<UserId> ids = [.. invitees ?? [InviteeId]];
        SessionDetails details = new(
            SessionTitle.Create("Raid night"),
            startTime ?? DefaultStart,
            Duration15.FromMinutes(durationMinutes),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance,
            maxCapacity,
            isPrivate);

        Session session = Session.Create(organizerId ?? OrganizerId, details, ids, Clock);
        _ = session.DequeueEvents();
        Sessions.Add(session);
        return session;
    }
}

/// <summary>
/// The in-memory read side used across Sessions tests: correct enough to prove the use-case wiring
/// (status filtering, visibility, roster shape) without re-proving A-072's exact keyset SQL, which is
/// covered by the real repository's own integration tests.
/// </summary>
internal sealed class FakeSessionReadRepository(FakeSessionRepository store) : ISessionReadRepository
{
    internal HashSet<UserId> AcceptedFriendsOf(UserId viewer) =>
        _friends.TryGetValue(viewer, out HashSet<UserId>? set) ? set : [];

    private readonly Dictionary<UserId, HashSet<UserId>> _friends = [];
    private readonly Dictionary<UserId, HashSet<UserId>> _blocked = [];

    internal void MakeFriends(UserId a, UserId b)
    {
        (_friends.TryGetValue(a, out HashSet<UserId>? setA) ? setA : _friends[a] = []).Add(b);
        (_friends.TryGetValue(b, out HashSet<UserId>? setB) ? setB : _friends[b] = []).Add(a);
    }

    internal void MakeBlocked(UserId a, UserId b)
    {
        (_blocked.TryGetValue(a, out HashSet<UserId>? setA) ? setA : _blocked[a] = []).Add(b);
        (_blocked.TryGetValue(b, out HashSet<UserId>? setB) ? setB : _blocked[b] = []).Add(a);
    }

    public Task<SessionDetail?> FindDetailAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        Session? session = store.All.FirstOrDefault(s => s.Id == sessionId);
        return Task.FromResult(session is null ? null : ToDetail(session));
    }

    public Task<SessionPage<SessionSummary>> ListUpcomingAsync(
        UserId viewerId, SessionListCursor? cursor, int pageSize, CancellationToken cancellationToken)
    {
        List<Session> visible = [.. store.All
            .Where(s => s.Status is not (SessionStatus.Completed or SessionStatus.Expired or SessionStatus.Canceled))
            .Where(s => IsVisible(viewerId, s))
            .OrderBy(s => s.StartTime)
            .ThenBy(s => s.Id.Value)];

        return Task.FromResult(Page(visible, pageSize, s => new SessionSummary(
            s.Id, s.OrganizerId, s.Title, s.StartTime, s.Duration, s.Platform, s.Game,
            s.Status, s.IsPrivate, s.MaxCapacity, s.ActiveOccupancy)));
    }

    public Task<SessionPage<SessionSummary>> ListHistoryAsync(
        UserId viewerId, SessionListCursor? cursor, int pageSize, CancellationToken cancellationToken)
    {
        List<Session> visible = [.. store.All
            .Where(s => s.Status == SessionStatus.Completed)
            .Where(s => IsVisible(viewerId, s))
            .OrderByDescending(s => s.StartTime)
            .ThenByDescending(s => s.Id.Value)];

        return Task.FromResult(Page(visible, pageSize, s => new SessionSummary(
            s.Id, s.OrganizerId, s.Title, s.StartTime, s.Duration, s.Platform, s.Game,
            s.Status, s.IsPrivate, s.MaxCapacity, s.ActiveOccupancy)));
    }

    public Task<SessionPage<JoinRequestSummary>> ListJoinRequestsAsync(
        SessionId sessionId, SessionListCursor? cursor, int pageSize, CancellationToken cancellationToken)
    {
        Session? session = store.All.FirstOrDefault(s => s.Id == sessionId);
        List<JoinRequest> requests = [.. (session?.JoinRequests ?? [])
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id.Value)];

        return Task.FromResult(Page(requests, pageSize, r => new JoinRequestSummary(
            r.Id, r.RequesterId, r.Status, r.CreatedAt)));
    }

    public Task<GuestSessionView?> FindGuestViewAsync(GuestLinkId guestLinkId, CancellationToken cancellationToken) =>
        Task.FromResult<GuestSessionView?>(null);

    private bool IsVisible(UserId viewerId, Session session)
    {
        Viewer viewer = Viewer.Create(viewerId, AcceptedFriendsOf(viewerId), BlockedOf(viewerId));
        return VisibilityEngine.ResolveSession(session, viewer).IsVisible;
    }

    private HashSet<UserId> BlockedOf(UserId viewer) =>
        _blocked.TryGetValue(viewer, out HashSet<UserId>? set) ? set : [];

    private static SessionPage<TOut> Page<TIn, TOut>(IReadOnlyList<TIn> items, int pageSize, Func<TIn, TOut> select)
    {
        List<TIn> taken = [.. items.Take(pageSize)];
        string? next = items.Count > pageSize ? "more" : null;
        return new SessionPage<TOut>([.. taken.Select(select)], next);
    }

    private static SessionDetail ToDetail(Session session)
    {
        List<RosterEntry> roster = [.. session.Participations.Select(p => new RosterEntry(
            p.Id, p.Participant, null, p.Status, p.IsWaitlisted, p.Origin, p.ReminderChoice))];

        return new SessionDetail(
            session.Id,
            session.OrganizerId,
            session.Title,
            session.StartTime,
            session.Duration,
            session.Platform,
            session.Game,
            session.Notes,
            session.MinAttendance,
            session.MaxCapacity,
            session.IsPrivate,
            session.Status,
            session.CreatedAt,
            roster,
            0);
    }
}
