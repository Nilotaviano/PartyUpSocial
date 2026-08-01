using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendRequestConcurrencyTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();
    private readonly MutableClock _clock = new(Instant.FromUtc(2026, 8, 1, 20, 0));
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
    public async Task Send_FromBothUsersConcurrently_EndsAcceptedOnce_D015(
        bool secondUserLocksFirst)
    {
        User firstActor = secondUserLocksFirst ? _users[1] : _users[0];
        User secondActor = secondUserLocksFirst ? _users[0] : _users[1];

        (FriendGraphCommandResult first, FriendGraphCommandResult second) =
            await RunForcedAsync(
                (context, locker) => FriendCommandIntegration.SendAsync(
                    context,
                    locker,
                    firstActor.Id,
                    secondActor.Username!.Value.Value,
                    _clock),
                (context, locker) => FriendCommandIntegration.SendAsync(
                    context,
                    locker,
                    secondActor.Id,
                    firstActor.Username!.Value.Value,
                    _clock));

        Assert.True(first.Succeeded);
        Assert.True(first.Created);
        Assert.True(second.Succeeded);
        Assert.Equal("accepted", second.Friendship!.Status);
        await AssertStateAsync(FriendshipStatus.Accepted, 1);
    }

    [Fact]
    public async Task Send_TwiceConcurrently_CreatesOneRequestAndOneNotification_D105()
    {
        (FriendGraphCommandResult first, FriendGraphCommandResult second) =
            await RunForcedAsync(
                (context, locker) => FriendCommandIntegration.SendAsync(
                    context,
                    locker,
                    _users[0].Id,
                    _users[1].Username!.Value.Value,
                    _clock),
                (context, locker) => FriendCommandIntegration.SendAsync(
                    context,
                    locker,
                    _users[0].Id,
                    _users[1].Username!.Value.Value,
                    _clock));

        Assert.True(first.Created);
        Assert.Equal("friend_request_already_exists", second.Code);
        await AssertStateAsync(FriendshipStatus.Pending, 1);
    }

    [Fact]
    public async Task UsernameChangesBeforePairLock_StaleNameCreatesNothing_D128()
    {
        string staleUsername = _users[1].Username!.Value.Value;
        await using AppDbContext sendContext = _database.CreateContext();
        var gate = new PairLockGate();
        var delayedLocker = new BeforeFriendPairLock(
            new FriendPairLocker(sendContext),
            gate);
        Task<FriendGraphCommandResult> send = FriendCommandIntegration.SendAsync(
            sendContext,
            delayedLocker,
            _users[0].Id,
            staleUsername,
            _clock);
        await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _clock.Advance(Duration.FromDays(31));
        await using (AppDbContext renameContext = _database.CreateContext())
        {
            var unitOfWork = new UnitOfWork(renameContext);
            await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    var users = new UserRepository(renameContext);
                    var usernames = new UsernameRegistryRepository(renameContext);
                    User target = (await users.FindForUpdateAsync(_users[1].Id, ct))!;
                    UsernameRegistryEntry current =
                        (await usernames.FindActiveForOwnerAsync(target.Id, ct))!;
                    string suffix = Guid.NewGuid().ToString("N")[..12];
                    Username next = Username.Create($"renamed{suffix}");
                    UsernameChangeResult change = UsernameRegistry.Change(
                        current,
                        null,
                        next,
                        target.Id,
                        _clock);
                    usernames.Add(change.ClaimedEntry);
                    target.ChangeUsername(next, _clock);
                    return true;
                },
                default);
        }

        gate.Release.TrySetResult();
        FriendGraphCommandResult result = await send;

        Assert.Equal("user_not_found", result.Code);
        await AssertStateAsync(null, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptRacingReject_BothOrderingsAssertFinalRowStatusAndNotificationCount_D105(
        bool rejectLocksFirst)
    {
        Friendship request = await SeedPendingAsync();

        (FriendGraphCommandResult first, FriendGraphCommandResult second) =
            await RunForcedAsync(
                rejectLocksFirst
                    ? (context, locker) => FriendCommandIntegration.RejectAsync(
                        context, locker, _users[1].Id, request.Id)
                    : (context, locker) => FriendCommandIntegration.AcceptAsync(
                        context, locker, _users[1].Id, request.Id, _clock),
                rejectLocksFirst
                    ? (context, locker) => FriendCommandIntegration.AcceptAsync(
                        context, locker, _users[1].Id, request.Id, _clock)
                    : (context, locker) => FriendCommandIntegration.RejectAsync(
                        context, locker, _users[1].Id, request.Id));

        Assert.True(first.Succeeded);
        Assert.Equal(
            rejectLocksFirst ? "friend_request_not_found" : "friend_request_not_pending",
            second.Code);
        await AssertStateAsync(rejectLocksFirst ? null : FriendshipStatus.Accepted, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptRacingCancel_BothOrderingsAssertFinalRowStatusAndNotificationCount_D105(
        bool cancelLocksFirst)
    {
        Friendship request = await SeedPendingAsync();

        (FriendGraphCommandResult first, FriendGraphCommandResult second) =
            await RunForcedAsync(
                cancelLocksFirst
                    ? (context, locker) => FriendCommandIntegration.CancelAsync(
                        context, locker, _users[0].Id, request.Id)
                    : (context, locker) => FriendCommandIntegration.AcceptAsync(
                        context, locker, _users[1].Id, request.Id, _clock),
                cancelLocksFirst
                    ? (context, locker) => FriendCommandIntegration.AcceptAsync(
                        context, locker, _users[1].Id, request.Id, _clock)
                    : (context, locker) => FriendCommandIntegration.CancelAsync(
                        context, locker, _users[0].Id, request.Id));

        Assert.True(first.Succeeded);
        Assert.Equal(
            cancelLocksFirst ? "friend_request_not_found" : "friend_request_not_pending",
            second.Code);
        await AssertStateAsync(cancelLocksFirst ? null : FriendshipStatus.Accepted, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptRacingUnfriend_BothOrderingsMatchTheRaceMatrix_D015(
        bool unfriendLocksFirst)
    {
        Friendship request = await SeedPendingAsync();

        (FriendGraphCommandResult first, FriendGraphCommandResult second) =
            await RunForcedAsync(
                unfriendLocksFirst
                    ? (context, locker) => FriendCommandIntegration.UnfriendAsync(
                        context, locker, _users[0].Id, request.Id)
                    : (context, locker) => FriendCommandIntegration.AcceptAsync(
                        context, locker, _users[1].Id, request.Id, _clock),
                unfriendLocksFirst
                    ? (context, locker) => FriendCommandIntegration.AcceptAsync(
                        context, locker, _users[1].Id, request.Id, _clock)
                    : (context, locker) => FriendCommandIntegration.UnfriendAsync(
                        context, locker, _users[0].Id, request.Id));

        if (unfriendLocksFirst)
        {
            Assert.Equal("friendship_not_found", first.Code);
            Assert.True(second.Succeeded);
        }
        else
        {
            Assert.True(first.Succeeded);
            Assert.True(second.Succeeded);
        }

        await AssertStateAsync(
            unfriendLocksFirst ? FriendshipStatus.Accepted : null,
            1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectRacingRerequest_BothOrderingsHaveExactNotificationCounts_D105(
        bool rejectLocksFirst)
    {
        Friendship request = await SeedPendingAsync();

        (FriendGraphCommandResult first, FriendGraphCommandResult second) =
            await RunForcedAsync(
                rejectLocksFirst
                    ? (context, locker) => FriendCommandIntegration.RejectAsync(
                        context, locker, _users[1].Id, request.Id)
                    : (context, locker) => FriendCommandIntegration.SendAsync(
                        context,
                        locker,
                        _users[0].Id,
                        _users[1].Username!.Value.Value,
                        _clock),
                rejectLocksFirst
                    ? (context, locker) => FriendCommandIntegration.SendAsync(
                        context,
                        locker,
                        _users[0].Id,
                        _users[1].Username!.Value.Value,
                        _clock)
                    : (context, locker) => FriendCommandIntegration.RejectAsync(
                        context, locker, _users[1].Id, request.Id));

        if (rejectLocksFirst)
        {
            Assert.True(first.Succeeded);
            Assert.True(second.Created);
        }
        else
        {
            Assert.Equal("friend_request_already_exists", first.Code);
            Assert.True(second.Succeeded);
        }

        await AssertStateAsync(
            rejectLocksFirst ? FriendshipStatus.Pending : null,
            rejectLocksFirst ? 2 : 1);
    }

    private async Task<Friendship> SeedPendingAsync()
    {
        Friendship request = Friendship.Request(_users[0], _users[1], _clock);
        FriendRequestReceived received =
            Assert.Single(request.DequeueEvents().OfType<FriendRequestReceived>());
        NotificationRequest notificationRequest = Assert.Single(
            RecipientComputer.ForFriendshipEvent(received, RecipientFilter.None));
        await using AppDbContext context = _database.CreateContext();
        context.Friendships.Add(request);
        context.Notifications.Add(Notification.From(notificationRequest, _clock));
        await context.SaveChangesAsync();
        return request;
    }

    private async Task<(FriendGraphCommandResult First, FriendGraphCommandResult Second)> RunForcedAsync(
        Func<AppDbContext, IFriendPairLocker, Task<FriendGraphCommandResult>> firstAction,
        Func<AppDbContext, IFriendPairLocker, Task<FriendGraphCommandResult>> secondAction)
    {
        await using AppDbContext firstContext = _database.CreateContext();
        await using AppDbContext secondContext = _database.CreateContext();
        var gate = new PairLockGate();
        var holdingLocker = new HoldingFriendPairLocker(
            new FriendPairLocker(firstContext),
            gate);

        Task<FriendGraphCommandResult> first = firstAction(firstContext, holdingLocker);
        await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<FriendGraphCommandResult> second =
            secondAction(secondContext, new FriendPairLocker(secondContext));
        gate.Release.TrySetResult();
        return (await first, await second);
    }

    private async Task AssertStateAsync(FriendshipStatus? status, int notificationCount)
    {
        await using AppDbContext context = _database.CreateContext();
        Friendship? friendship = await context.Friendships.AsNoTracking().SingleOrDefaultAsync();
        if (status is null)
        {
            Assert.Null(friendship);
        }
        else
        {
            Assert.Equal(status, friendship!.Status);
        }

        Assert.Equal(notificationCount, await context.Notifications.CountAsync());
    }
}

internal sealed class PairLockGate
{
    internal TaskCompletionSource Held { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class HoldingFriendPairLocker(
    IFriendPairLocker inner,
    PairLockGate gate) : IFriendPairLocker
{
    public async Task<LockedFriendPair?> LockAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        LockedFriendPair? pair = await inner.LockAsync(first, second, cancellationToken);
        gate.Held.TrySetResult();
        await gate.Release.Task.WaitAsync(cancellationToken);
        return pair;
    }
}

internal sealed class BeforeFriendPairLock(
    IFriendPairLocker inner,
    PairLockGate gate) : IFriendPairLocker
{
    public async Task<LockedFriendPair?> LockAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        gate.Held.TrySetResult();
        await gate.Release.Task.WaitAsync(cancellationToken);
        return await inner.LockAsync(first, second, cancellationToken);
    }
}

internal static class FriendCommandIntegration
{
    internal static async Task EnsureUsernameRegistryAsync(
        FriendGraphDatabase database,
        IEnumerable<User> users,
        IClock clock)
    {
        await using AppDbContext context = database.CreateContext();
        context.UsernameRegistry.AddRange(users.Select(user =>
            UsernameRegistry.Claim(null, user.Username!.Value, user.Id, clock)));
        await context.SaveChangesAsync();
    }

    internal static Task<FriendGraphCommandResult> SendAsync(
        AppDbContext context,
        IFriendPairLocker locker,
        UserId actorId,
        string targetUsername,
        IClock clock) =>
        new SendFriendRequestUseCase(
                new UserGraphResolver(context),
                locker,
                new FriendshipRepository(context),
                new BlockRepository(context),
                new NotificationRepository(context),
                new UnitOfWork(context),
                clock)
            .ExecuteAsync(actorId, targetUsername, default);

    internal static Task<FriendGraphCommandResult> AcceptAsync(
        AppDbContext context,
        IFriendPairLocker locker,
        UserId actorId,
        FriendshipId friendshipId,
        IClock clock) =>
        new AcceptFriendRequestUseCase(
                locker,
                new FriendshipRepository(context),
                new BlockRepository(context),
                new UnitOfWork(context),
                clock)
            .ExecuteAsync(actorId, friendshipId, default);

    internal static Task<FriendGraphCommandResult> RejectAsync(
        AppDbContext context,
        IFriendPairLocker locker,
        UserId actorId,
        FriendshipId friendshipId) =>
        new RejectFriendRequestUseCase(
                locker,
                new FriendshipRepository(context),
                new BlockRepository(context),
                new UnitOfWork(context))
            .ExecuteAsync(actorId, friendshipId, default);

    internal static Task<FriendGraphCommandResult> CancelAsync(
        AppDbContext context,
        IFriendPairLocker locker,
        UserId actorId,
        FriendshipId friendshipId) =>
        new CancelFriendRequestUseCase(
                locker,
                new FriendshipRepository(context),
                new BlockRepository(context),
                new UnitOfWork(context))
            .ExecuteAsync(actorId, friendshipId, default);

    internal static Task<FriendGraphCommandResult> UnfriendAsync(
        AppDbContext context,
        IFriendPairLocker locker,
        UserId actorId,
        FriendshipId friendshipId) =>
        new UnfriendUseCase(
                locker,
                new FriendshipRepository(context),
                new BlockRepository(context),
                new UnitOfWork(context))
            .ExecuteAsync(actorId, friendshipId, default);
}
