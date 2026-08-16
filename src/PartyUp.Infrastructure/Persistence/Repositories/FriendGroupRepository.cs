using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// Locking reads follow the two-step idiom the friend graph already uses: a raw lock-only statement,
/// then a plain LINQ hydrate — <see cref="FriendGroup"/> carries an <c>xmin</c> concurrency shadow
/// property a <c>FromSql</c> hydrate would not satisfy. The group row takes <c>FOR NO KEY UPDATE</c>
/// (<see cref="SessionLockRepository"/>'s parent idiom: roster writes are children, not key changes);
/// membership rows take <c>FOR UPDATE</c> like <see cref="FriendPairLocker"/>'s pair rows.
/// </summary>
public sealed class FriendGroupRepository(AppDbContext context) : IFriendGroupRepository
{
    public void Add(FriendGroup group) => context.FriendGroups.Add(group);

    public void Remove(FriendGroup group) => context.FriendGroups.Remove(group);

    public Task<FriendGroup?> FindNoTrackingAsync(FriendGroupId id, CancellationToken cancellationToken) =>
        context.FriendGroups
            .AsNoTracking()
            .Include(group => group.Members)
            .SingleOrDefaultAsync(group => group.Id == id, cancellationToken);

    public async Task<FriendGroup?> LoadForUpdateAsync(FriendGroupId id, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM friend_groups
            WHERE id = {id.Value}
            FOR NO KEY UPDATE
            """, cancellationToken);

        return await context.FriendGroups
            .Include(group => group.Members)
            .SingleOrDefaultAsync(group => group.Id == id, cancellationToken);
    }

    public Task<int> CountOwnedForUpdateAsync(UserId ownerId, CancellationToken cancellationToken) =>
        context.FriendGroups.CountAsync(group => group.OwnerId == ownerId, cancellationToken);

    public async Task<IReadOnlyCollection<FriendGroupId>> LockViewerGroupIdsAsync(
        UserId viewerId,
        IReadOnlyCollection<FriendGroupId> candidateGroupIds,
        CancellationToken cancellationToken)
    {
        if (candidateGroupIds.Count == 0)
        {
            return [];
        }

        FriendGroupId[] candidates = [.. candidateGroupIds];
        Guid[] ids = [.. candidateGroupIds.Select(id => id.Value)];

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM friend_groups
            WHERE id = ANY({ids})
            ORDER BY id
            FOR NO KEY UPDATE
            """, cancellationToken);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM friend_group_members
            WHERE group_id = ANY({ids}) AND user_id = {viewerId.Value}
            ORDER BY group_id, user_id
            FOR UPDATE
            """, cancellationToken);

        return await GrantingGroupIdsAsync(viewerId, candidates, cancellationToken);
    }

    public async Task<IReadOnlyCollection<FriendGroupId>> GetViewerGroupIdsAsync(
        UserId viewerId,
        IReadOnlyCollection<FriendGroupId> candidateGroupIds,
        CancellationToken cancellationToken)
    {
        if (candidateGroupIds.Count == 0)
        {
            return [];
        }

        return await GrantingGroupIdsAsync(viewerId, [.. candidateGroupIds], cancellationToken);
    }

    private async Task<IReadOnlyCollection<FriendGroupId>> GrantingGroupIdsAsync(
        UserId viewerId,
        FriendGroupId[] candidates,
        CancellationToken cancellationToken)
    {
        List<FriendGroupId> granted = await context.FriendGroups
            .AsNoTracking()
            .Where(group =>
                candidates.Contains(group.Id) &&
                (group.OwnerId == viewerId ||
                 group.Members.Any(member =>
                     member.UserId == viewerId && member.Status == FriendGroupMemberStatus.Accepted)))
            .Select(group => group.Id)
            .ToListAsync(cancellationToken);

        return granted;
    }

    public Task<GroupName?> FindNameAsync(FriendGroupId id, CancellationToken cancellationToken) =>
        context.FriendGroups
            .AsNoTracking()
            .Where(group => group.Id == id)
            .Select(group => group.Name)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyCollection<FriendGroup>> ListInvolvingAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        await context.FriendGroups
            .AsNoTracking()
            .Include(group => group.Members)
            .Where(group => group.OwnerId == userId || group.Members.Any(member => member.UserId == userId))
            .OrderBy(group => group.Id)
            .ToListAsync(cancellationToken);

    /// <summary>D-205's deletion-time read, raw SQL over the two audience junction tables (neither has its own DbSet).</summary>
    public async Task<IReadOnlyCollection<UserId>> ListDistinctOrganizersTargetingAsync(
        FriendGroupId groupId,
        CancellationToken cancellationToken)
    {
        Guid id = groupId.Value;
        List<Guid> organizerIds = await context.Database.SqlQuery<Guid>($"""
            SELECT organizer_id AS "Value"
            FROM sessions s
            JOIN session_audience_groups sag ON sag.session_id = s.id
            WHERE sag.group_id = {id}
            UNION
            SELECT organizer_id AS "Value"
            FROM polls p
            JOIN poll_audience_groups pag ON pag.poll_id = p.id
            WHERE pag.group_id = {id}
            """).ToListAsync(cancellationToken);

        return [.. organizerIds.Select(UserId.From)];
    }
}
