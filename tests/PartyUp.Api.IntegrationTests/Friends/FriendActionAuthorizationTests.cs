using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendActionAuthorizationTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();
    private readonly MutableClock _clock = new(Instant.FromUtc(2026, 8, 1, 22, 0));
    private User[] _users = [];
    private FriendshipId _friendshipId;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _users = await _database.SeedUsersAsync(3, _clock);
        Friendship friendship = Friendship.Request(_users[0], _users[1], _clock);
        _ = friendship.DequeueEvents();
        _friendshipId = friendship.Id;
        await using AppDbContext context = _database.CreateContext();
        context.Friendships.Add(friendship);
        await context.SaveChangesAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData("accept")]
    [InlineData("reject")]
    [InlineData("cancel")]
    [InlineData("unfriend")]
    public async Task FriendshipIdProbeRequiresActorMembershipBeforeUserLocks_D065(
        string operation)
    {
        ProbeResult unauthorized = await ExecuteAsync(operation, _friendshipId);
        ProbeResult missing = await ExecuteAsync(operation, FriendshipId.New());

        string expected = operation == "unfriend"
            ? "friendship_not_found"
            : "friend_request_not_found";
        Assert.Equal(expected, unauthorized.Result.Code);
        Assert.Equal(expected, missing.Result.Code);
        Assert.Equal(["probe"], unauthorized.Calls);
        Assert.Equal(["probe"], missing.Calls);
        Assert.Equal(0, unauthorized.UserLockCalls);
        Assert.Equal(0, missing.UserLockCalls);
    }

    private async Task<ProbeResult> ExecuteAsync(string operation, FriendshipId friendshipId)
    {
        await using AppDbContext context = _database.CreateContext();
        List<string> calls = [];
        var repository = new ProbeFriendshipRepository(
            new FriendshipRepository(context),
            calls);
        var locker = new ProbeFriendPairLocker(
            new FriendPairLocker(context),
            calls);
        var blocks = new BlockRepository(context);
        var unitOfWork = new UnitOfWork(context);
        FriendGraphCommandResult result = operation switch
        {
            "accept" => await new AcceptFriendRequestUseCase(
                    locker, repository, blocks, unitOfWork, _clock)
                .ExecuteAsync(_users[2].Id, friendshipId, default),
            "reject" => await new RejectFriendRequestUseCase(
                    locker, repository, blocks, unitOfWork)
                .ExecuteAsync(_users[2].Id, friendshipId, default),
            "cancel" => await new CancelFriendRequestUseCase(
                    locker, repository, blocks, unitOfWork)
                .ExecuteAsync(_users[2].Id, friendshipId, default),
            "unfriend" => await new UnfriendUseCase(
                    locker, repository, blocks, unitOfWork)
                .ExecuteAsync(_users[2].Id, friendshipId, default),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        return new ProbeResult(result, calls, locker.Calls);
    }

    private sealed record ProbeResult(
        FriendGraphCommandResult Result,
        IReadOnlyList<string> Calls,
        int UserLockCalls);
}

internal sealed class ProbeFriendPairLocker(
    IFriendPairLocker inner,
    List<string> calls) : IFriendPairLocker
{
    internal int Calls { get; private set; }

    public Task<LockedFriendPair?> LockAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        Calls++;
        calls.Add("users");
        return inner.LockAsync(first, second, cancellationToken);
    }
}

internal sealed class ProbeFriendshipRepository(
    IFriendshipRepository inner,
    List<string> calls) : IFriendshipRepository
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
        CancellationToken cancellationToken)
    {
        calls.Add("probe");
        return inner.FindOwnedPairAsync(friendshipId, actorId, cancellationToken);
    }

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

    public void Remove(Friendship friendship) => inner.Remove(friendship);
}
