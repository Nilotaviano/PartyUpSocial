using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

public interface IBlockRepository
{
    Task<IReadOnlyList<Block>> FindPairForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken);

    Task<BlockPair?> FindOutboundOwnedPairAsync(
        BlockId blockId,
        UserId blockerId,
        CancellationToken cancellationToken);

    Task<Block?> FindByIdForUpdateAsync(
        BlockId blockId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BlockedUserProjection>> ListOutboundAsync(
        UserId blockerId,
        CancellationToken cancellationToken);

    void Add(Block block);

    void Add(Block block, Username blockedUsernameSnapshot) =>
        throw new NotSupportedException("Block persistence must store the at-block username snapshot.");

    void Remove(Block block);
}

public sealed record BlockPair(UserId BlockerId, UserId BlockedId);

public sealed record BlockedUserProjection(
    BlockId BlockId,
    string BlockedUsernameSnapshot,
    Instant CreatedAt);
