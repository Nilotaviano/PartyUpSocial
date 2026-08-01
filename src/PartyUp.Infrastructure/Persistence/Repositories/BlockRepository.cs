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
        var rows = await (
            from block in context.Blocks.AsNoTracking()
            where block.BlockerId == blockerId
            join counterpart in context.DomainUsers.AsNoTracking() on block.BlockedId equals counterpart.Id
            where counterpart.Username != null
            orderby block.CreatedAt descending, block.Id descending
            select new { block.Id, Counterpart = counterpart, block.CreatedAt })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new BlockedUserProjection(
            row.Id,
            row.Counterpart.Username!.Value.Value,
            row.CreatedAt)).ToArray();
    }

    public void Add(Block block) => context.Blocks.Add(block);

    public void Remove(Block block) => context.Blocks.Remove(block);
}
