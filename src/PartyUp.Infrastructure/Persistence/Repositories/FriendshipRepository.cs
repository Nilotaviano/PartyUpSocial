using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class FriendshipRepository(AppDbContext context) : IFriendshipRepository
{
    public Task<Friendship?> FindPairAsync(UserId first, UserId second, CancellationToken cancellationToken)
    {
        (UserId lo, UserId hi) = Friendship.CanonicalPair(first, second);
        return context.Friendships.AsNoTracking().SingleOrDefaultAsync(
            friendship => friendship.UserLoId == lo && friendship.UserHiId == hi,
            cancellationToken);
    }

    public async Task<Friendship?> FindPairForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        (UserId lo, UserId hi) = Friendship.CanonicalPair(first, second);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM friendships WHERE user_lo_id = {lo.Value} AND user_hi_id = {hi.Value} FOR UPDATE",
            cancellationToken);
        return await context.Friendships.SingleOrDefaultAsync(
            friendship => friendship.UserLoId == lo && friendship.UserHiId == hi,
            cancellationToken);
    }

    /// <summary>Non-locking bulk read (F6) — replaces a per-counterpart <see cref="FindPairAsync"/> loop on a GET path.</summary>
    public async Task<IReadOnlyList<Friendship>> FindPairsAmongAsync(
        UserId userId,
        IReadOnlyCollection<UserId> otherUserIds,
        CancellationToken cancellationToken) =>
        await QueryPairsAmong(userId, otherUserIds).AsNoTracking().ToListAsync(cancellationToken);

    /// <summary>
    /// The A-068 bulk graph lock (§6.1 step 3): one statement, the table's declared order. A raw
    /// lock-only statement first, then a plain LINQ hydrate — <see cref="Friendship"/> carries an
    /// <c>xmin</c> concurrency shadow property that a <c>SELECT *</c>-based <c>FromSql</c> hydrate does
    /// not satisfy, the same two-step idiom <see cref="FindPairForUpdateAsync"/> already uses.
    /// </summary>
    public async Task<IReadOnlyList<Friendship>> LockPairsAsync(
        UserId userId,
        IReadOnlyCollection<UserId> otherUserIds,
        CancellationToken cancellationToken)
    {
        if (otherUserIds.Count == 0)
        {
            return [];
        }

        Guid[] others = [.. otherUserIds.Select(id => id.Value)];
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM friendships
            WHERE (user_lo_id = {userId.Value} AND user_hi_id = ANY({others}))
               OR (user_hi_id = {userId.Value} AND user_lo_id = ANY({others}))
            ORDER BY user_lo_id, user_hi_id, id
            FOR UPDATE
            """, cancellationToken);

        return await QueryPairsAmong(userId, otherUserIds).ToListAsync(cancellationToken);
    }

    private IQueryable<Friendship> QueryPairsAmong(UserId userId, IReadOnlyCollection<UserId> otherUserIds) =>
        context.Friendships.Where(f =>
            (f.UserLoId == userId && otherUserIds.Contains(f.UserHiId)) ||
            (f.UserHiId == userId && otherUserIds.Contains(f.UserLoId)));

    public Task<FriendshipPair?> FindOwnedPairAsync(
        FriendshipId friendshipId,
        UserId actorId,
        CancellationToken cancellationToken) =>
        context.Friendships.AsNoTracking()
            .Where(friendship => friendship.Id == friendshipId &&
                (friendship.UserLoId == actorId || friendship.UserHiId == actorId))
            .Select(friendship => new FriendshipPair(friendship.UserLoId, friendship.UserHiId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<Friendship?> FindByIdForUpdateAsync(
        FriendshipId friendshipId,
        CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM friendships WHERE id = {friendshipId.Value} FOR UPDATE",
            cancellationToken);
        return await context.Friendships.SingleOrDefaultAsync(
            friendship => friendship.Id == friendshipId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<AcceptedFriendProjection>> ListAcceptedAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from friendship in context.Friendships.AsNoTracking()
            where friendship.Status == FriendshipStatus.Accepted &&
                  (friendship.UserLoId == userId || friendship.UserHiId == userId)
            let counterpartId = friendship.UserLoId == userId ? friendship.UserHiId : friendship.UserLoId
            join counterpart in context.DomainUsers.AsNoTracking() on counterpartId equals counterpart.Id
            orderby counterpart.DisplayName, counterpart.Username, friendship.Id
            select new { friendship.Id, Counterpart = counterpart })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new AcceptedFriendProjection(row.Id, row.Counterpart)).ToArray();
    }

    public async Task<IReadOnlyList<PendingFriendProjection>> ListPendingAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from friendship in context.Friendships.AsNoTracking()
            where friendship.Status == FriendshipStatus.Pending &&
                  (friendship.UserLoId == userId || friendship.UserHiId == userId)
            let counterpartId = friendship.UserLoId == userId ? friendship.UserHiId : friendship.UserLoId
            join counterpart in context.DomainUsers.AsNoTracking() on counterpartId equals counterpart.Id
            orderby friendship.RequestedAt descending, friendship.Id descending
            select new { friendship.Id, Counterpart = counterpart, friendship.RequestedBy, friendship.RequestedAt })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new PendingFriendProjection(
            row.Id,
            row.Counterpart,
            row.RequestedBy,
            row.RequestedAt)).ToArray();
    }

    public void Add(Friendship friendship) => context.Friendships.Add(friendship);

    public void Remove(Friendship friendship) => context.Friendships.Remove(friendship);
}
