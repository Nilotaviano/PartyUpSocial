using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Friends;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendBlockConcurrencyTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();
    private readonly MutableClock _clock = new(Instant.FromUtc(2026, 8, 1, 23, 0));
    private User[] _users = [];

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _users = await _database.SeedUsersAsync(2, _clock);
        await FriendCommandIntegration.EnsureUsernameRegistryAsync(_database, _users, _clock);
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockRacingAccept_LeavesBlockAndNoFriendship_D065(bool blockLocksFirst)
    {
        Friendship request = await SeedPendingAsync();
        var counts = new BlockEffectCounts();
        await using AppDbContext firstContext = _database.CreateContext();
        await using AppDbContext secondContext = _database.CreateContext();
        var gate = new PairLockGate();
        var attemptGate = new PairLockAttemptGate();

        if (blockLocksFirst)
        {
            Task<BlockGraphCommandResult> block = BlockAsync(
                firstContext,
                new HoldingFriendPairLocker(new FriendPairLocker(firstContext), gate),
                _users[0].Id,
                _users[1].Username!.Value.Value,
                counts);
            await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task<FriendGraphCommandResult> accept = FriendCommandIntegration.AcceptAsync(
                secondContext,
                new AttemptingFriendPairLocker(
                    new FriendPairLocker(secondContext),
                    attemptGate),
                _users[1].Id,
                request.Id,
                _clock);
            await AssertBlockedAttemptAsync(accept, attemptGate, gate);

            Assert.True((await block).Succeeded);
            Assert.Equal("friend_request_not_found", (await accept).Code);
        }
        else
        {
            Task<FriendGraphCommandResult> accept = FriendCommandIntegration.AcceptAsync(
                firstContext,
                new HoldingFriendPairLocker(new FriendPairLocker(firstContext), gate),
                _users[1].Id,
                request.Id,
                _clock);
            await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task<BlockGraphCommandResult> block = BlockAsync(
                secondContext,
                new AttemptingFriendPairLocker(
                    new FriendPairLocker(secondContext),
                    attemptGate),
                _users[0].Id,
                _users[1].Username!.Value.Value,
                counts);
            await AssertBlockedAttemptAsync(block, attemptGate, gate);

            Assert.True((await accept).Succeeded);
            Assert.True((await block).Succeeded);
        }

        await AssertFinalStateAsync(_users[0].Id, _users[1].Id);
        Assert.Equal(1, counts.Executions);
        Assert.Equal(1, counts.ScopeLoads);
        Assert.Equal(1, counts.FriendshipDeletes);
        Assert.Equal(0, counts.ReminderWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OppositeBlocksRacing_ExposeNoDirectionAndLeaveNoFriendship_D065(
        bool secondDirectionLocksFirst)
    {
        await SeedAcceptedAsync();
        User firstActor = secondDirectionLocksFirst ? _users[1] : _users[0];
        User secondActor = secondDirectionLocksFirst ? _users[0] : _users[1];
        var counts = new BlockEffectCounts();

        (BlockGraphCommandResult winner, BlockGraphCommandResult loser) =
            await RunBlockRaceAsync(firstActor, secondActor, counts);

        Assert.True(winner.Succeeded);
        Assert.True(winner.Created);
        Assert.Equal("user_not_found", loser.Code);
        await AssertFinalStateAsync(firstActor.Id, secondActor.Id);
        Assert.Equal(1, counts.Executions);
        Assert.Equal(1, counts.ScopeLoads);
        Assert.Equal(1, counts.FriendshipDeletes);
        Assert.Equal(0, counts.ReminderWrites);
    }

    [Fact]
    public async Task SameDirectionBlocksRacing_CreateOneRowOneSuccessOneHidden404AndApplyEffectsOnce_D065()
    {
        await SeedAcceptedAsync();
        var counts = new BlockEffectCounts();

        (BlockGraphCommandResult winner, BlockGraphCommandResult loser) =
            await RunBlockRaceAsync(_users[0], _users[0], counts);

        Assert.True(winner.Succeeded);
        Assert.True(winner.Created);
        Assert.Equal("user_not_found", loser.Code);
        await AssertFinalStateAsync(_users[0].Id, _users[1].Id);
        Assert.Equal(1, counts.Executions);
        Assert.Equal(1, counts.ScopeLoads);
        Assert.Equal(1, counts.FriendshipDeletes);
        Assert.Equal(0, counts.ReminderWrites);
    }

    [Fact]
    public async Task Phase3ReminderWriter_ThrowsOnAnyUnexpectedCancellation_D077()
    {
        var writer = new NoSessionReminderScheduleWriter();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.ApplyAsync(
                new SessionReminderCanceled(SessionId.New(), UserId.New()),
                default));
    }

    private async Task<Friendship> SeedPendingAsync()
    {
        Friendship friendship = Friendship.Request(_users[0], _users[1], _clock);
        _ = friendship.DequeueEvents();
        await using AppDbContext context = _database.CreateContext();
        context.Friendships.Add(friendship);
        await context.SaveChangesAsync();
        return friendship;
    }

    private async Task SeedAcceptedAsync()
    {
        Friendship friendship = Friendship.Request(_users[0], _users[1], _clock);
        friendship.Accept(_users[1].Id, _clock);
        _ = friendship.DequeueEvents();
        await using AppDbContext context = _database.CreateContext();
        context.Friendships.Add(friendship);
        await context.SaveChangesAsync();
    }

    private async Task<(BlockGraphCommandResult Winner, BlockGraphCommandResult Loser)>
        RunBlockRaceAsync(User firstActor, User secondActor, BlockEffectCounts counts)
    {
        User firstTarget = firstActor.Id == _users[0].Id ? _users[1] : _users[0];
        User secondTarget = secondActor.Id == _users[0].Id ? _users[1] : _users[0];
        await using AppDbContext firstContext = _database.CreateContext();
        await using AppDbContext secondContext = _database.CreateContext();
        var gate = new PairLockGate();
        var attemptGate = new PairLockAttemptGate();
        Task<BlockGraphCommandResult> first = BlockAsync(
            firstContext,
            new HoldingFriendPairLocker(new FriendPairLocker(firstContext), gate),
            firstActor.Id,
            firstTarget.Username!.Value.Value,
            counts);
        await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<BlockGraphCommandResult> second = BlockAsync(
            secondContext,
            new AttemptingFriendPairLocker(
                new FriendPairLocker(secondContext),
                attemptGate),
            secondActor.Id,
            secondTarget.Username!.Value.Value,
            counts);
        await AssertBlockedAttemptAsync(second, attemptGate, gate);
        return (await first, await second);
    }

    private Task<BlockGraphCommandResult> BlockAsync(
        AppDbContext context,
        IFriendPairLocker locker,
        UserId actorId,
        string targetUsername,
        BlockEffectCounts counts)
    {
        var friendships = new CountingFriendshipRepository(
            new FriendshipRepository(context),
            counts);
        var executor = new CountingBlockEffectsExecutor(
            new BlockEffectsExecutor(
                new CountingEmptyBlockEffectScopeProvider(counts),
                friendships,
                new CountingReminderScheduleWriter(counts),
                _clock),
            counts);
        return new BlockUserUseCase(
                new UserGraphResolver(context),
                locker,
                friendships,
                new BlockRepository(context),
                executor,
                new UnitOfWork(context),
                _clock)
            .ExecuteAsync(actorId, targetUsername, default);
    }

    private static async Task AssertBlockedAttemptAsync(
        Task task,
        PairLockAttemptGate attemptGate,
        PairLockGate holdingGate)
    {
        try
        {
            await attemptGate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(task.IsCompleted);
        }
        finally
        {
            holdingGate.Release.TrySetResult();
        }
    }

    private async Task AssertFinalStateAsync(UserId expectedBlocker, UserId expectedBlocked)
    {
        await using AppDbContext context = _database.CreateContext();
        Assert.Equal(0, await context.Friendships.CountAsync());
        Assert.Equal(0, await context.Notifications.CountAsync());
        Block block = await context.Blocks.AsNoTracking().SingleAsync();
        Assert.Equal(expectedBlocker, block.BlockerId);
        Assert.Equal(expectedBlocked, block.BlockedId);
    }
}

internal sealed class BlockEffectCounts
{
    private int _executions;
    private int _scopeLoads;
    private int _friendshipDeletes;
    private int _reminderWrites;

    internal int Executions => Volatile.Read(ref _executions);
    internal int ScopeLoads => Volatile.Read(ref _scopeLoads);
    internal int FriendshipDeletes => Volatile.Read(ref _friendshipDeletes);
    internal int ReminderWrites => Volatile.Read(ref _reminderWrites);

    internal void RecordExecution() => Interlocked.Increment(ref _executions);
    internal void RecordScopeLoad() => Interlocked.Increment(ref _scopeLoads);
    internal void RecordFriendshipDelete() => Interlocked.Increment(ref _friendshipDeletes);
    internal void RecordReminderWrite() => Interlocked.Increment(ref _reminderWrites);
}

internal sealed class CountingBlockEffectsExecutor(
    IBlockEffectsExecutor inner,
    BlockEffectCounts counts) : IBlockEffectsExecutor
{
    public Task ExecuteAsync(
        Block block,
        IReadOnlyList<Friendship> pairFriendships,
        CancellationToken cancellationToken)
    {
        counts.RecordExecution();
        return inner.ExecuteAsync(block, pairFriendships, cancellationToken);
    }
}

internal sealed class CountingEmptyBlockEffectScopeProvider(BlockEffectCounts counts)
    : IBlockEffectScopeProvider
{
    public Task<BlockEffectScope> LoadForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        counts.RecordScopeLoad();
        return Task.FromResult(BlockEffectScope.Empty);
    }
}

internal sealed class CountingReminderScheduleWriter(BlockEffectCounts counts)
    : IReminderScheduleWriter
{
    public Task ApplyAsync(
        SessionReminderCanceled cancellation,
        CancellationToken cancellationToken)
    {
        counts.RecordReminderWrite();
        return Task.CompletedTask;
    }
}

internal sealed class CountingFriendshipRepository(
    IFriendshipRepository inner,
    BlockEffectCounts counts) : IFriendshipRepository
{
    public Task<Friendship?> FindPairAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken) =>
        inner.FindPairAsync(first, second, cancellationToken);

    public Task<Friendship?> FindPairForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken) =>
        inner.FindPairForUpdateAsync(first, second, cancellationToken);

    public Task<FriendshipPair?> FindOwnedPairAsync(
        FriendshipId friendshipId,
        UserId actorId,
        CancellationToken cancellationToken) =>
        inner.FindOwnedPairAsync(friendshipId, actorId, cancellationToken);

    public Task<Friendship?> FindByIdForUpdateAsync(
        FriendshipId friendshipId,
        CancellationToken cancellationToken) =>
        inner.FindByIdForUpdateAsync(friendshipId, cancellationToken);

    public Task<IReadOnlyList<AcceptedFriendProjection>> ListAcceptedAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        inner.ListAcceptedAsync(userId, cancellationToken);

    public Task<IReadOnlyList<PendingFriendProjection>> ListPendingAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        inner.ListPendingAsync(userId, cancellationToken);

    public void Add(Friendship friendship) => inner.Add(friendship);

    public void Remove(Friendship friendship)
    {
        counts.RecordFriendshipDelete();
        inner.Remove(friendship);
    }
}
