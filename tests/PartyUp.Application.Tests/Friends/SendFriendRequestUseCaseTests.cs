using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Tests.Friends;

public sealed class SendFriendRequestUseCaseTests
{
    [Fact]
    public async Task Execute_CreatesOnePendingCanonicalPair_D015()
    {
        var fixture = new FriendCommandFixture();

        FriendGraphCommandResult result = await fixture.SendAsync();

        Assert.True(result.Succeeded);
        Assert.True(result.Created);
        Assert.Equal("pending", result.Friendship!.Status);
        Assert.Equal("outgoing", result.Friendship.Direction);
        Assert.Equal(fixture.Target.Username!.Value.Value, result.Friendship.CounterpartUsername);
        Friendship friendship = Assert.IsType<Friendship>(fixture.Friendships.Existing);
        Assert.Equal(fixture.Actor.Id, friendship.RequestedBy);
        Notification notification = Assert.Single(fixture.Notifications.Added);
        Assert.Equal(fixture.Target.Id, notification.RecipientUserId);
        Assert.Equal(fixture.Actor.Id, notification.ActorUserId);
        Assert.Equal(NotificationType.FriendRequestReceived, notification.Type);
        Assert.Equal(["users", "friendship", "blocks", "notification"], fixture.Calls);
        Assert.Equal(1, fixture.UnitOfWork.Executions);
    }

    [Fact]
    public async Task Execute_AutoAcceptsAnIncomingPendingRequest_D015()
    {
        var fixture = new FriendCommandFixture();
        fixture.SetExisting(Friendship.Request(fixture.Target, fixture.Actor, fixture.Clock));

        FriendGraphCommandResult result = await fixture.SendAsync();

        Assert.True(result.Succeeded);
        Assert.False(result.Created);
        Assert.Equal("accepted", result.Friendship!.Status);
        Assert.Equal("accepted", result.Friendship.Direction);
        Assert.True(fixture.Friendships.Existing!.IsAccepted);
        Assert.Empty(fixture.Notifications.Added);
    }

    [Fact]
    public async Task Execute_MaterializesTheReceivedNotificationInTheUnitOfWork_D105()
    {
        var fixture = new FriendCommandFixture();

        FriendGraphCommandResult result = await fixture.SendAsync();

        Assert.True(result.Created);
        Notification notification = Assert.Single(fixture.Notifications.Added);
        Assert.Equal(fixture.Target.Id, notification.RecipientUserId);
        Assert.Equal(fixture.Actor.Id, notification.ActorUserId);
        Assert.Equal(1, fixture.UnitOfWork.Executions);
    }

    [Fact]
    public async Task Execute_DoesNotNotifyCrossedAcceptance_D105()
    {
        var fixture = new FriendCommandFixture();
        fixture.SetExisting(Friendship.Request(fixture.Target, fixture.Actor, fixture.Clock));

        FriendGraphCommandResult result = await fixture.SendAsync();

        Assert.Equal("accepted", result.Friendship!.Status);
        Assert.Empty(fixture.Notifications.Added);
    }

    [Fact]
    public async Task Execute_OutgoingAndAcceptedRowsReturnTheirDistinctConflicts_D015()
    {
        var outgoing = new FriendCommandFixture();
        outgoing.SetExisting(Friendship.Request(outgoing.Actor, outgoing.Target, outgoing.Clock));

        FriendGraphCommandResult duplicate = await outgoing.SendAsync();

        Assert.Equal("friend_request_already_exists", duplicate.Code);

        var accepted = new FriendCommandFixture();
        Friendship friendship = Friendship.Request(accepted.Actor, accepted.Target, accepted.Clock);
        friendship.Accept(accepted.Target.Id, accepted.Clock);
        accepted.SetExisting(friendship);

        FriendGraphCommandResult existing = await accepted.SendAsync();

        Assert.Equal("friendship_already_exists", existing.Code);
        Assert.Empty(accepted.Notifications.Added);
    }

    [Fact]
    public async Task Execute_SelfHiddenAndBlockedTargetsWriteNothing_D065()
    {
        var self = new FriendCommandFixture();
        self.Resolver.Target = null;

        FriendGraphCommandResult selfResult = await self.SendAsync(
            self.Actor.Username!.Value.Value);

        Assert.Equal("friend_request_self", selfResult.Code);
        Assert.Empty(self.Calls);

        var hidden = new FriendCommandFixture();
        hidden.Resolver.Target = null;
        FriendGraphCommandResult hiddenResult = await hidden.SendAsync();
        Assert.Equal("user_not_found", hiddenResult.Code);

        var blocked = new FriendCommandFixture();
        blocked.Blocks.Rows =
        [
            Block.Create(
                blocked.Actor.Id,
                blocked.Target.Id,
                blocked.Target.Username!.Value,
                blocked.Clock),
        ];
        FriendGraphCommandResult blockedResult = await blocked.SendAsync();
        Assert.Equal("user_not_found", blockedResult.Code);
        Assert.Null(blocked.Friendships.Existing);
        Assert.Empty(blocked.Notifications.Added);
    }
}

internal sealed class FriendCommandFixture
{
    internal FriendCommandFixture()
    {
        Actor = FriendTestData.Active("actor-user", "Actor");
        Target = FriendTestData.Active("target-user", "Target");
        Resolver = new CommandResolver(Actor, Target);
        PairLocker = new CommandPairLocker(Actor, Target, Calls);
        Friendships = new CommandFriendshipRepository(Calls);
        Blocks = new CommandBlockRepository(Calls);
        Notifications = new CommandNotificationRepository(Calls);
        UnitOfWork = new CommandUnitOfWork();
    }

    internal User Actor { get; }

    internal User Target { get; }

    internal IClock Clock { get; } = FriendTestData.Clock();

    internal List<string> Calls { get; } = [];

    internal CommandResolver Resolver { get; }

    internal CommandPairLocker PairLocker { get; }

    internal CommandFriendshipRepository Friendships { get; }

    internal CommandBlockRepository Blocks { get; }

    internal CommandNotificationRepository Notifications { get; }

    internal CommandUnitOfWork UnitOfWork { get; }

    internal Task<FriendGraphCommandResult> SendAsync(string? username = null) =>
        new SendFriendRequestUseCase(
                Resolver,
                PairLocker,
                Friendships,
                Blocks,
                Notifications,
                UnitOfWork,
                Clock)
            .ExecuteAsync(Actor.Id, username ?? Target.Username!.Value.Value, default);

    internal void SetExisting(Friendship friendship)
    {
        _ = friendship.DequeueEvents();
        Friendships.Existing = friendship;
    }
}

internal sealed class CommandResolver(User actor, User initialTarget) : IUserGraphResolver
{
    internal User? Target { get; set; } = initialTarget;

    public Task<ResolvedUserGraph?> ResolveSelfAsync(
        UserId viewerId,
        CancellationToken cancellationToken) =>
        Task.FromResult<ResolvedUserGraph?>(
            FriendTestData.Resolved(actor, ProfileDisclosure.Full));

    public Task<ResolvedUserGraph?> ResolveFriendTargetByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken) =>
        Task.FromResult(Target is null
            ? null
            : FriendTestData.Resolved(Target, ProfileDisclosure.UsernameOnly));

    public Task<ResolvedUserGraph?> ResolveProfileByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken) => Task.FromResult<ResolvedUserGraph?>(null);

    public Task<ResolvedUserGraph?> ResolveProfileByFriendCodeAsync(
        UserId viewerId,
        string canonicalFriendCode,
        CancellationToken cancellationToken) => Task.FromResult<ResolvedUserGraph?>(null);

    public Task<ResolvedUserGraph?> ResolveBlockTargetByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken) => Task.FromResult<ResolvedUserGraph?>(null);
}

internal sealed class CommandPairLocker(
    User first,
    User second,
    List<string> calls) : IFriendPairLocker
{
    internal int Calls { get; private set; }

    public Task<LockedFriendPair?> LockAsync(
        UserId firstId,
        UserId secondId,
        CancellationToken cancellationToken)
    {
        Calls++;
        calls.Add("users");
        return Task.FromResult<LockedFriendPair?>(new LockedFriendPair(first, second));
    }
}

internal sealed class CommandFriendshipRepository(List<string> calls) : IFriendshipRepository
{
    internal Friendship? Existing { get; set; }

    public Task<Friendship?> FindPairAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken) => Task.FromResult(Existing);

    public Task<Friendship?> FindPairForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        calls.Add("friendship");
        return Task.FromResult(Existing);
    }

    public Task<FriendshipPair?> FindOwnedPairAsync(
        FriendshipId friendshipId,
        UserId actorId,
        CancellationToken cancellationToken)
    {
        FriendshipPair? pair = Existing is not null &&
                               Existing.Id == friendshipId &&
                               Existing.Involves(actorId)
            ? new FriendshipPair(Existing.UserLoId, Existing.UserHiId)
            : null;
        return Task.FromResult(pair);
    }

    public Task<Friendship?> FindByIdForUpdateAsync(
        FriendshipId friendshipId,
        CancellationToken cancellationToken)
    {
        calls.Add("friendship");
        return Task.FromResult(Existing?.Id == friendshipId ? Existing : null);
    }

    public Task<IReadOnlyList<AcceptedFriendProjection>> ListAcceptedAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AcceptedFriendProjection>>([]);

    public Task<IReadOnlyList<PendingFriendProjection>> ListPendingAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingFriendProjection>>([]);

    public void Add(Friendship friendship) => Existing = friendship;

    public void Remove(Friendship friendship) => Existing = null;
}

internal sealed class CommandBlockRepository(List<string> calls) : IBlockRepository
{
    internal IReadOnlyList<Block> Rows { get; set; } = [];

    public Task<IReadOnlyList<Block>> FindPairForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        calls.Add("blocks");
        return Task.FromResult(Rows);
    }

    public Task<BlockPair?> FindOutboundOwnedPairAsync(
        BlockId blockId,
        UserId blockerId,
        CancellationToken cancellationToken) => Task.FromResult<BlockPair?>(null);

    public Task<Block?> FindByIdForUpdateAsync(
        BlockId blockId,
        CancellationToken cancellationToken) => Task.FromResult<Block?>(null);

    public Task<IReadOnlyList<BlockedUserProjection>> ListOutboundAsync(
        UserId blockerId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BlockedUserProjection>>([]);

    public void Add(Block block)
    {
    }

    public void Remove(Block block)
    {
    }
}

internal sealed class CommandNotificationRepository(List<string> calls) : INotificationRepository
{
    internal List<Notification> Added { get; } = [];

    public void Add(Notification notification)
    {
        calls.Add("notification");
        Added.Add(notification);
    }

    public Task AddSteamMatchIfAbsentAsync(
        Notification notification,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteForRecipientAsync(
        UserId recipientUserId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class CommandUnitOfWork : IUnitOfWork
{
    internal int Executions { get; private set; }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        Executions++;
        return await work(cancellationToken);
    }
}
