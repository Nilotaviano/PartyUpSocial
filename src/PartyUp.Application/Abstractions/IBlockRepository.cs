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

    /// <summary>
    /// A non-locking single-pair read (PHASE4_DESIGN.md §6.1 step 1 — resolving authorization must
    /// never take a row lock ahead of the A-068 lock steps). The default forwards to
    /// <see cref="FindPairForUpdateAsync"/> for a repository that has not been widened with a
    /// dedicated read query; a widened repository overrides this with a plain, no-lock query.
    /// </summary>
    Task<IReadOnlyList<Block>> FindPairAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken) =>
        FindPairForUpdateAsync(first, second, cancellationToken);

    /// <summary>
    /// A non-locking bulk read of every block row between <paramref name="userId"/> and any of
    /// <paramref name="otherUserIds"/> — the set-based replacement for a per-counterpart
    /// <see cref="FindPairAsync"/> loop on a GET path (§6.1, F6). The default composes
    /// <see cref="FindPairAsync"/> once per counterpart; a widened repository overrides this with one
    /// SQL statement.
    /// </summary>
    Task<IReadOnlyList<Block>> FindPairsAmongAsync(
        UserId userId,
        IReadOnlyCollection<UserId> otherUserIds,
        CancellationToken cancellationToken) =>
        CollectPairsAsync(userId, otherUserIds, locking: false, cancellationToken);

    /// <summary>
    /// The A-068 bulk graph lock (§6.1 step 3): locks and returns every block row between
    /// <paramref name="userId"/> and any of <paramref name="otherUserIds"/> in one statement, the
    /// table's declared order. The default composes <see cref="FindPairForUpdateAsync"/> once per
    /// counterpart (correct, not single-statement); a widened repository overrides this with the real
    /// bulk-ordered lock.
    /// </summary>
    Task<IReadOnlyList<Block>> LockPairsAsync(
        UserId userId,
        IReadOnlyCollection<UserId> otherUserIds,
        CancellationToken cancellationToken) =>
        CollectPairsAsync(userId, otherUserIds, locking: true, cancellationToken);

    private async Task<IReadOnlyList<Block>> CollectPairsAsync(
        UserId userId,
        IReadOnlyCollection<UserId> otherUserIds,
        bool locking,
        CancellationToken cancellationToken)
    {
        List<Block> results = [];
        foreach (UserId other in otherUserIds)
        {
            IReadOnlyList<Block> pair = locking
                ? await FindPairForUpdateAsync(userId, other, cancellationToken)
                : await FindPairAsync(userId, other, cancellationToken);
            results.AddRange(pair);
        }

        return results;
    }

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

    void Remove(Block block);
}

public sealed record BlockPair(UserId BlockerId, UserId BlockedId);

public sealed record BlockedUserProjection(
    BlockId BlockId,
    string BlockedUsernameSnapshot,
    Instant CreatedAt);
