using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Deletion;

public sealed class DeletionScopeProvider(
    AppDbContext context,
    IUsernameRegistryRepository usernames)
    : IDeletionScopeProvider
{
    public async Task<DeletionScope> LoadAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM friendships
            WHERE user_lo_id = {id.Value} OR user_hi_id = {id.Value}
            ORDER BY user_lo_id, user_hi_id, id
            FOR UPDATE
            """, cancellationToken);

        List<Friendship> friendships = await context.Friendships
            .Where(friendship => friendship.UserLoId == id || friendship.UserHiId == id)
            .OrderBy(friendship => friendship.UserLoId)
            .ThenBy(friendship => friendship.UserHiId)
            .ThenBy(friendship => friendship.Id)
            .ToListAsync(cancellationToken);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM blocks
            WHERE blocker_id = {id.Value} OR blocked_id = {id.Value}
            ORDER BY LEAST(blocker_id, blocked_id),
                     GREATEST(blocker_id, blocked_id),
                     blocker_id,
                     blocked_id,
                     id
            FOR UPDATE
            """, cancellationToken);

        List<Block> loadedBlocks = await context.Blocks
            .Where(block => block.BlockerId == id || block.BlockedId == id)
            .ToListAsync(cancellationToken);
        Block[] blocks = loadedBlocks
            .OrderBy(block => Lower(block.BlockerId, block.BlockedId).Value)
            .ThenBy(block => Upper(block.BlockerId, block.BlockedId).Value)
            .ThenBy(block => block.BlockerId.Value)
            .ThenBy(block => block.BlockedId.Value)
            .ThenBy(block => block.Id.Value)
            .ToArray();

        UsernameRegistryEntry? username = await usernames.FindActiveForOwnerAsync(
            id,
            cancellationToken);
        return new DeletionScope([], [], friendships, blocks, username);
    }

    private static UserId Lower(UserId first, UserId second) =>
        first.Value.CompareTo(second.Value) < 0 ? first : second;

    private static UserId Upper(UserId first, UserId second) =>
        first.Value.CompareTo(second.Value) > 0 ? first : second;
}
