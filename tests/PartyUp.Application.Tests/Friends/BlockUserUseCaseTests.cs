using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Tests.Friends;

public sealed class BlockUserUseCaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_DeletesPendingOrAcceptedFriendship_D065(bool accepted)
    {
        var fixture = new BlockUserFixture();
        fixture.SetFriendship(accepted);

        BlockGraphCommandResult result = await fixture.ExecuteAsync();

        Assert.True(result.Succeeded);
        Assert.True(result.Created);
        Assert.Null(fixture.Friendships.Existing);
        Assert.Single(fixture.Friendships.Removed);
        Assert.Single(fixture.Blocks.Rows);
        Assert.Equal(
            ["users", "friendship", "blocks", "effects", "block-add"],
            fixture.Calls);
    }

    [Fact]
    public async Task Execute_CreatesNoNotification_D105()
    {
        var fixture = new BlockUserFixture();

        BlockGraphCommandResult result = await fixture.ExecuteAsync();

        Assert.True(result.Succeeded);
        Block block = Assert.Single(fixture.Blocks.Rows);
        Assert.All(
            block.DequeueEvents(),
            domainEvent => Assert.Empty(
                RecipientComputer.ForFriendshipEvent(domainEvent, RecipientFilter.None)));
    }

    [Fact]
    public async Task Execute_AlwaysInvokesTheBlockEffectsExecutor_D077()
    {
        var fixture = new BlockUserFixture();

        BlockGraphCommandResult result = await fixture.ExecuteAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, fixture.Effects.Calls);
        Assert.Empty(Assert.Single(fixture.Effects.FriendshipScopes));
    }

    [Fact]
    public async Task Execute_WithThePhase3EmptyScope_ChangesNoSessionOrPoll_D077()
    {
        var fixture = new BlockUserFixture();
        fixture.SetFriendship(accepted: true);
        Session session = BlockExecutorTestData.Session(
            fixture.Actor.Id,
            fixture.Target.Id,
            fixture.Clock,
            makeGoing: true);
        Poll poll = BlockExecutorTestData.Poll(
            fixture.Actor.Id,
            fixture.Target.Id,
            fixture.Clock);
        ParticipationStatus sessionStatus = session.Find(
            ParticipantRef.ForUser(fixture.Target.Id))!.Status;
        bool pollParticipantWasActive = poll.FindParticipant(
            ParticipantRef.ForUser(fixture.Target.Id))!.IsActive;
        var scope = new TrackingEmptyBlockEffectScopeProvider();
        var executor = new BlockEffectsExecutor(
            scope,
            fixture.Friendships,
            new RecordingReminderScheduleWriter(),
            fixture.Clock);

        BlockGraphCommandResult result = await fixture.ExecuteAsync(executor);

        Assert.True(result.Succeeded);
        Assert.Equal(1, scope.Calls);
        Assert.Null(fixture.Friendships.Existing);
        Assert.Equal(
            sessionStatus,
            session.Find(ParticipantRef.ForUser(fixture.Target.Id))!.Status);
        Assert.Equal(
            pollParticipantWasActive,
            poll.FindParticipant(ParticipantRef.ForUser(fixture.Target.Id))!.IsActive);
    }
}

internal sealed class BlockUserFixture
{
    internal BlockUserFixture()
    {
        Actor = FriendTestData.Active("block-actor", "Block Actor");
        Target = FriendTestData.Active("block-target", "Block Target");
        Resolver = new BlockTestResolver(Actor, Target);
        PairLocker = new BlockTestPairLocker(Actor, Target, Calls);
        Friendships = new BlockTestFriendshipRepository(Calls);
        Blocks = new BlockTestBlockRepository(Calls);
        Effects = new RecordingBlockEffectsExecutor(Friendships, Calls);
        UnitOfWork = new BlockTestUnitOfWork();
    }

    internal User Actor { get; }

    internal User Target { get; }

    internal IClock Clock { get; } = FriendTestData.Clock();

    internal List<string> Calls { get; } = [];

    internal BlockTestResolver Resolver { get; }

    internal BlockTestPairLocker PairLocker { get; }

    internal BlockTestFriendshipRepository Friendships { get; }

    internal BlockTestBlockRepository Blocks { get; }

    internal RecordingBlockEffectsExecutor Effects { get; }

    internal BlockTestUnitOfWork UnitOfWork { get; }

    internal Task<BlockGraphCommandResult> ExecuteAsync(IBlockEffectsExecutor? executor = null) =>
        new BlockUserUseCase(
                Resolver,
                PairLocker,
                Friendships,
                Blocks,
                executor ?? Effects,
                UnitOfWork,
                Clock)
            .ExecuteAsync(Actor.Id, Target.Username!.Value.Value, default);

    internal void SetFriendship(bool accepted)
    {
        Friendship friendship = Friendship.Request(Actor, Target, Clock);
        if (accepted)
        {
            friendship.Accept(Target.Id, Clock);
        }

        _ = friendship.DequeueEvents();
        Friendships.Existing = friendship;
    }
}

internal sealed class BlockTestResolver(User self, User target) : IUserGraphResolver
{
    public Task<ResolvedUserGraph?> ResolveSelfAsync(
        UserId viewerId,
        CancellationToken cancellationToken) =>
        Task.FromResult<ResolvedUserGraph?>(FriendTestData.Resolved(self, ProfileDisclosure.Full));

    public Task<ResolvedUserGraph?> ResolveBlockTargetByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken) =>
        Task.FromResult<ResolvedUserGraph?>(
            FriendTestData.Resolved(target, ProfileDisclosure.UsernameOnly));

    public Task<ResolvedUserGraph?> ResolveProfileByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken) => Task.FromResult<ResolvedUserGraph?>(null);

    public Task<ResolvedUserGraph?> ResolveProfileByFriendCodeAsync(
        UserId viewerId,
        string canonicalFriendCode,
        CancellationToken cancellationToken) => Task.FromResult<ResolvedUserGraph?>(null);

    public Task<ResolvedUserGraph?> ResolveFriendTargetByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken) => Task.FromResult<ResolvedUserGraph?>(null);
}

internal sealed class BlockTestPairLocker(
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

internal sealed class BlockTestFriendshipRepository(List<string> calls) : IFriendshipRepository
{
    internal Friendship? Existing { get; set; }

    internal List<Friendship> Removed { get; } = [];

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
        CancellationToken cancellationToken) => Task.FromResult<FriendshipPair?>(null);

    public Task<Friendship?> FindByIdForUpdateAsync(
        FriendshipId friendshipId,
        CancellationToken cancellationToken) => Task.FromResult<Friendship?>(null);

    public Task<IReadOnlyList<AcceptedFriendProjection>> ListAcceptedAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AcceptedFriendProjection>>([]);

    public Task<IReadOnlyList<PendingFriendProjection>> ListPendingAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingFriendProjection>>([]);

    public void Add(Friendship friendship) => Existing = friendship;

    public void Remove(Friendship friendship)
    {
        Removed.Add(friendship);
        if (ReferenceEquals(Existing, friendship))
        {
            Existing = null;
        }
    }
}

internal sealed class BlockTestBlockRepository(List<string> calls) : IBlockRepository
{
    internal List<Block> Rows { get; } = [];

    public Task<IReadOnlyList<Block>> FindPairForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        calls.Add("blocks");
        return Task.FromResult<IReadOnlyList<Block>>(
            Rows.Where(block => block.Hides(first, second)).ToArray());
    }

    public Task<BlockPair?> FindOutboundOwnedPairAsync(
        BlockId blockId,
        UserId blockerId,
        CancellationToken cancellationToken)
    {
        calls.Add("probe");
        Block? row = Rows.SingleOrDefault(block => block.Id == blockId && block.BlockerId == blockerId);
        return Task.FromResult(row is null ? null : new BlockPair(row.BlockerId, row.BlockedId));
    }

    public Task<Block?> FindByIdForUpdateAsync(
        BlockId blockId,
        CancellationToken cancellationToken)
    {
        calls.Add("block");
        return Task.FromResult(Rows.SingleOrDefault(block => block.Id == blockId));
    }

    public Task<IReadOnlyList<BlockedUserProjection>> ListOutboundAsync(
        UserId blockerId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BlockedUserProjection>>([]);

    public void Add(Block block)
    {
        calls.Add("block-add");
        Rows.Add(block);
    }

    public void Remove(Block block)
    {
        calls.Add("block-remove");
        Rows.Remove(block);
    }
}

internal sealed class RecordingBlockEffectsExecutor(
    IFriendshipRepository friendships,
    List<string> calls) : IBlockEffectsExecutor
{
    internal int Calls { get; private set; }

    internal List<IReadOnlyList<Friendship>> FriendshipScopes { get; } = [];

    public Task ExecuteAsync(
        Block block,
        IReadOnlyList<Friendship> pairFriendships,
        CancellationToken cancellationToken)
    {
        Calls++;
        calls.Add("effects");
        FriendshipScopes.Add(pairFriendships);
        foreach (Friendship friendship in pairFriendships)
        {
            friendships.Remove(friendship);
        }

        return Task.CompletedTask;
    }
}

internal sealed class BlockTestUnitOfWork : IUnitOfWork
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

internal sealed class TrackingEmptyBlockEffectScopeProvider : IBlockEffectScopeProvider
{
    internal int Calls { get; private set; }

    public Task<BlockEffectScope> LoadForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(BlockEffectScope.Empty);
    }
}
