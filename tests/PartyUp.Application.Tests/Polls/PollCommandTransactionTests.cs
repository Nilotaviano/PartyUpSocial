using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Polls;
using PartyUp.Application.Tests.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Polls;

/// <summary>
/// A-016 proof, mirroring <c>SessionCommandTransactionTests</c>: the aggregate write and the
/// notification materialization sit inside one <see cref="IUnitOfWork.ExecuteAsync{T}"/> — a thrown
/// failure mid-materialization rolls the aggregate write back too, so no poll ever exists without its
/// creation notifications (and vice versa).
/// </summary>
public sealed class PollCommandTransactionTests
{
    [Fact]
    public async Task Create_RollsBackPollAndNotification_A016()
    {
        FakePollRepository polls = new();
        FakeThrowingPollNotificationMaterializer materializer = new();
        RollbackAwarePollUnitOfWork unitOfWork = new(polls);
        FakeFriendshipRepository friendships = new();
        FakeBlockRepository blocks = new();
        MutableClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));

        (UserId organizerId, UserId inviteeId) = friendships.MakeAccepted(clock);

        var useCase = new CreatePollUseCase(
            polls, new FakePollLockRepository(), friendships, blocks, materializer, unitOfWork, clock);

        CreatePollCommand command = new(
            organizerId,
            "Raid poll",
            DurationMinutes: 60,
            Platform: null,
            Game: null,
            Notes: null,
            MinAttendance: 1,
            MaxCapacity: null,
            IsPrivate: false,
            OptionStartTimes: [clock.GetCurrentInstant() + Duration.FromHours(5), clock.GetCurrentInstant() + Duration.FromHours(6)],
            DeadlineChoice: null,
            InviteeUserIds: [inviteeId],
            GuestLinkCount: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecuteAsync(command, default));

        Assert.Empty(polls.All);
        Assert.Equal(1, unitOfWork.Executions);
    }
}

internal sealed class FakeThrowingPollNotificationMaterializer : IPollNotificationMaterializer
{
    public Task MaterializeAsync(Poll poll, IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Simulated notification-write failure.");
}

internal sealed class FakePollRepository : IPollRepository
{
    private Dictionary<PollId, Poll> _polls = [];

    internal IReadOnlyCollection<Poll> All => _polls.Values;

    public void Add(Poll poll) => _polls[poll.Id] = poll;

    public void RemoveOptionAndCascadedVotes(PollOption deletedOption, IReadOnlyList<PollVote> cascadedVotes)
    {
        // No-op: this fake never tracks options/votes as separate rows.
    }

    public Task<Poll?> FindAsync(PollId id, CancellationToken cancellationToken) =>
        Task.FromResult(_polls.TryGetValue(id, out Poll? poll) ? poll : null);

    public Task<Poll?> FindNoTrackingAsync(PollId id, CancellationToken cancellationToken) => FindAsync(id, cancellationToken);

    public Task<PollForUpdate?> LoadForUpdateAsync(PollId id, CancellationToken cancellationToken) =>
        Task.FromResult(_polls.TryGetValue(id, out Poll? poll) ? new PollForUpdate(poll) : null);

    internal Dictionary<PollId, Poll> Snapshot() => new(_polls);

    internal void Restore(Dictionary<PollId, Poll> snapshot) => _polls = new(snapshot);
}

internal sealed class FakePollLockRepository : IPollLockRepository
{
    public Task LockUsersAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task LockPollsAsync(IReadOnlyCollection<PollId> pollIds, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RollbackAwarePollUnitOfWork(FakePollRepository polls) : IUnitOfWork
{
    internal int Executions { get; private set; }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        Executions++;
        Dictionary<PollId, Poll> snapshot = polls.Snapshot();
        try
        {
            return await work(cancellationToken);
        }
        catch
        {
            polls.Restore(snapshot);
            throw;
        }
    }
}

internal sealed class FakeFriendshipRepository : IFriendshipRepository
{
    internal List<Friendship> Items { get; } = [];

    public Task<Friendship?> FindPairAsync(UserId first, UserId second, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(f => f.IsBetween(first, second)));

    public Task<Friendship?> FindPairForUpdateAsync(UserId first, UserId second, CancellationToken cancellationToken) =>
        FindPairAsync(first, second, cancellationToken);

    public Task<FriendshipPair?> FindOwnedPairAsync(FriendshipId friendshipId, UserId actorId, CancellationToken cancellationToken) =>
        Task.FromResult<FriendshipPair?>(null);

    public Task<Friendship?> FindByIdForUpdateAsync(FriendshipId friendshipId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(f => f.Id == friendshipId));

    public Task<IReadOnlyList<AcceptedFriendProjection>> ListAcceptedAsync(UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AcceptedFriendProjection>>([]);

    public Task<IReadOnlyList<PendingFriendProjection>> ListPendingAsync(UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingFriendProjection>>([]);

    public void Add(Friendship friendship) => Items.Add(friendship);

    public void Remove(Friendship friendship) => Items.Remove(friendship);

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

    public Task<IReadOnlyList<Block>> FindPairForUpdateAsync(UserId first, UserId second, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Block>>(
            [.. Items.Where(b =>
                (b.BlockerId == first && b.BlockedId == second) ||
                (b.BlockerId == second && b.BlockedId == first))]);

    public Task<BlockPair?> FindOutboundOwnedPairAsync(BlockId blockId, UserId blockerId, CancellationToken cancellationToken) =>
        Task.FromResult<BlockPair?>(null);

    public Task<Block?> FindByIdForUpdateAsync(BlockId blockId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(b => b.Id == blockId));

    public Task<IReadOnlyList<BlockedUserProjection>> ListOutboundAsync(UserId blockerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BlockedUserProjection>>([]);

    public void Add(Block block) => Items.Add(block);

    public void Remove(Block block) => Items.Remove(block);
}

internal sealed class MutableClock(Instant current) : IClock
{
    public Instant GetCurrentInstant() => current;

    public void Advance(Duration duration) => current += duration;
}
