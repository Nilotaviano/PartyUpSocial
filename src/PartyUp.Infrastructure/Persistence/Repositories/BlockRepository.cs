using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class BlockRepository(AppDbContext context) : IBlockRepository
{
    public async Task<IReadOnlyList<Block>> FindPairForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken) =>
        await context.Blocks
            .FromSqlInterpolated($"""
                SELECT *
                FROM blocks
                WHERE (blocker_id = {first.Value} AND blocked_id = {second.Value})
                   OR (blocker_id = {second.Value} AND blocked_id = {first.Value})
                ORDER BY LEAST(blocker_id, blocked_id),
                         GREATEST(blocker_id, blocked_id),
                         blocker_id,
                         blocked_id,
                         id
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

    /// <summary>Non-locking single-pair read (§6.1 step 1) — no row lock ever taken here.</summary>
    public async Task<IReadOnlyList<Block>> FindPairAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken) =>
        await context.Blocks
            .FromSqlInterpolated($"""
                SELECT *
                FROM blocks
                WHERE (blocker_id = {first.Value} AND blocked_id = {second.Value})
                   OR (blocker_id = {second.Value} AND blocked_id = {first.Value})
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    /// <summary>Non-locking bulk read (F6) — replaces a per-counterpart <see cref="FindPairAsync"/> loop on a GET path.</summary>
    public async Task<IReadOnlyList<Block>> FindPairsAmongAsync(
        UserId userId,
        IReadOnlyCollection<UserId> otherUserIds,
        CancellationToken cancellationToken)
    {
        if (otherUserIds.Count == 0)
        {
            return [];
        }

        Guid[] others = [.. otherUserIds.Select(id => id.Value)];
        return await context.Blocks
            .FromSqlInterpolated($"""
                SELECT *
                FROM blocks
                WHERE (blocker_id = {userId.Value} AND blocked_id = ANY({others}))
                   OR (blocked_id = {userId.Value} AND blocker_id = ANY({others}))
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>The A-068 bulk graph lock (§6.1 step 3): one statement, the table's declared order.</summary>
    public async Task<IReadOnlyList<Block>> LockPairsAsync(
        UserId userId,
        IReadOnlyCollection<UserId> otherUserIds,
        CancellationToken cancellationToken)
    {
        if (otherUserIds.Count == 0)
        {
            return [];
        }

        Guid[] others = [.. otherUserIds.Select(id => id.Value)];
        return await context.Blocks
            .FromSqlInterpolated($"""
                SELECT *
                FROM blocks
                WHERE (blocker_id = {userId.Value} AND blocked_id = ANY({others}))
                   OR (blocked_id = {userId.Value} AND blocker_id = ANY({others}))
                ORDER BY LEAST(blocker_id, blocked_id),
                         GREATEST(blocker_id, blocked_id),
                         blocker_id,
                         blocked_id,
                         id
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
    }

    public Task<BlockPair?> FindOutboundOwnedPairAsync(
        BlockId blockId,
        UserId blockerId,
        CancellationToken cancellationToken) =>
        context.Blocks.AsNoTracking()
            .Where(block => block.Id == blockId && block.BlockerId == blockerId)
            .Select(block => new BlockPair(block.BlockerId, block.BlockedId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<Block?> FindByIdForUpdateAsync(
        BlockId blockId,
        CancellationToken cancellationToken) =>
        context.Blocks
            .FromSqlInterpolated($"SELECT * FROM blocks WHERE id = {blockId.Value} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<BlockedUserProjection>> ListOutboundAsync(
        UserId blockerId,
        CancellationToken cancellationToken)
    {
        Block[] rows = await context.Blocks.AsNoTracking()
            .Where(block => block.BlockerId == blockerId)
            .OrderByDescending(block => block.CreatedAt)
            .ThenByDescending(block => block.Id)
            .ToArrayAsync(cancellationToken);

        return rows.Select(block => new BlockedUserProjection(
            block.Id,
            block.BlockedUsernameSnapshot.Value,
            block.CreatedAt)).ToArray();
    }

    public void Add(Block block) => context.Blocks.Add(block);

    public void Remove(Block block) => context.Blocks.Remove(block);
}
