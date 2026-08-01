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

    public Task<Friendship?> FindPairForUpdateAsync(UserId first, UserId second, CancellationToken cancellationToken)
    {
        (UserId lo, UserId hi) = Friendship.CanonicalPair(first, second);
        return context.Friendships
            .FromSqlInterpolated($"SELECT * FROM friendships WHERE user_lo_id = {lo.Value} AND user_hi_id = {hi.Value} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<FriendshipPair?> FindOwnedPairAsync(
        FriendshipId friendshipId,
        UserId actorId,
        CancellationToken cancellationToken) =>
        context.Friendships.AsNoTracking()
            .Where(friendship => friendship.Id == friendshipId &&
                (friendship.UserLoId == actorId || friendship.UserHiId == actorId))
            .Select(friendship => new FriendshipPair(friendship.UserLoId, friendship.UserHiId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<Friendship?> FindByIdForUpdateAsync(
        FriendshipId friendshipId,
        CancellationToken cancellationToken) =>
        context.Friendships
            .FromSqlInterpolated($"SELECT * FROM friendships WHERE id = {friendshipId.Value} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

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
