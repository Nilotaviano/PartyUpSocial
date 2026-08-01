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
public sealed class BlockActionAuthorizationTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();
    private readonly MutableClock _clock = new(Instant.FromUtc(2026, 8, 1, 23, 30));
    private User[] _users = [];
    private BlockId _blockId;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _users = await _database.SeedUsersAsync(3, _clock);
        Block block = Block.Create(_users[0].Id, _users[1].Id, _clock);
        _blockId = block.Id;
        await using AppDbContext context = _database.CreateContext();
        context.Blocks.Add(block);
        await context.SaveChangesAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task BlockIdProbeRequiresOutboundOwnershipBeforeUserLocks_D065()
    {
        ProbeResult unauthorized = await ExecuteAsync(_blockId);
        ProbeResult missing = await ExecuteAsync(BlockId.New());

        Assert.Equal("block_not_found", unauthorized.Result.Code);
        Assert.Equal("block_not_found", missing.Result.Code);
        Assert.Equal(["probe"], unauthorized.Calls);
        Assert.Equal(["probe"], missing.Calls);
        Assert.Equal(0, unauthorized.UserLockCalls);
        Assert.Equal(0, missing.UserLockCalls);
    }

    private async Task<ProbeResult> ExecuteAsync(BlockId blockId)
    {
        await using AppDbContext context = _database.CreateContext();
        List<string> calls = [];
        var repository = new ProbeBlockRepository(new BlockRepository(context), calls);
        var locker = new ProbeFriendPairLocker(new FriendPairLocker(context), calls);
        BlockGraphCommandResult result = await new UnblockUserUseCase(
                locker,
                repository,
                new UnitOfWork(context))
            .ExecuteAsync(_users[2].Id, blockId, default);
        return new ProbeResult(result, calls, locker.Calls);
    }

    private sealed record ProbeResult(
        BlockGraphCommandResult Result,
        IReadOnlyList<string> Calls,
        int UserLockCalls);
}

internal sealed class ProbeBlockRepository(
    IBlockRepository inner,
    List<string> calls) : IBlockRepository
{
    public Task<IReadOnlyList<Block>> FindPairForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken) =>
        inner.FindPairForUpdateAsync(first, second, cancellationToken);

    public Task<BlockPair?> FindOutboundOwnedPairAsync(
        BlockId blockId,
        UserId blockerId,
        CancellationToken cancellationToken)
    {
        calls.Add("probe");
        return inner.FindOutboundOwnedPairAsync(blockId, blockerId, cancellationToken);
    }

    public Task<Block?> FindByIdForUpdateAsync(
        BlockId blockId,
        CancellationToken cancellationToken) =>
        inner.FindByIdForUpdateAsync(blockId, cancellationToken);

    public Task<IReadOnlyList<BlockedUserProjection>> ListOutboundAsync(
        UserId blockerId,
        CancellationToken cancellationToken) =>
        inner.ListOutboundAsync(blockerId, cancellationToken);

    public void Add(Block block) => inner.Add(block);

    public void Remove(Block block) => inner.Remove(block);
}
