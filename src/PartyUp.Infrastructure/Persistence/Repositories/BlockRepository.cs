using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class BlockRepository(AppDbContext context) : IBlockRepository
{
    private const string _blockedUsernameSnapshot = "BlockedUsernameSnapshot";

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
        => await context.Blocks.AsNoTracking()
            .Where(block => block.BlockerId == blockerId)
            .OrderByDescending(block => block.CreatedAt)
            .ThenByDescending(block => block.Id)
            .Select(block => new BlockedUserProjection(
                block.Id,
                EF.Property<string>(block, _blockedUsernameSnapshot),
                block.CreatedAt))
            .ToArrayAsync(cancellationToken);

    public void Add(Block block) =>
        throw new NotSupportedException("Block persistence must store the at-block username snapshot.");

    public void Add(Block block, Username blockedUsernameSnapshot)
    {
        context.Blocks.Add(block);
        context.Entry(block).Property<string>(_blockedUsernameSnapshot).CurrentValue =
            blockedUsernameSnapshot.Value;
    }

    public void Remove(Block block) => context.Blocks.Remove(block);
}
