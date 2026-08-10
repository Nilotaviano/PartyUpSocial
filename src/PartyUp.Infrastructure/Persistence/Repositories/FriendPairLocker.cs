using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// A-069: the pair lock is bounded by <c>lock_timeout</c>, so contention raises <c>55P03</c>
/// (<c>lock_not_available</c>) — a transient conflict <see cref="UnitOfWork"/> retries in a fresh
/// transaction — instead of blocking until the ambient token is cancelled. <c>SET LOCAL</c> is
/// required rather than <c>SET</c>: it reverts at commit/rollback, so the setting cannot leak to
/// unrelated work that later borrows this pooled connection. Every caller runs inside
/// <see cref="IUnitOfWork.ExecuteAsync{T}"/>'s transaction, which is what makes the <c>SET LOCAL</c>
/// and the <c>FOR UPDATE</c> below share one connection and one transaction.
/// </summary>
public sealed class FriendPairLocker(AppDbContext context) : IFriendPairLocker
{
    /// <summary>
    /// Two orders of magnitude above a normal hold of these rows (a friend command commits a
    /// handful of statements), and low enough that A-069's three attempts stay inside any request
    /// budget: worst case 3 x 1 s plus the 20/50 ms backoff rungs.
    /// </summary>
    internal const string LockTimeoutInterval = "1000ms";

    private const string SetLockTimeoutSql = $"SET LOCAL lock_timeout = '{LockTimeoutInterval}'";

    public async Task<LockedFriendPair?> LockAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        if (first == second)
        {
            return null;
        }

        // A compile-time constant, never caller input: Postgres accepts no bind parameter in SET.
        await context.Database.ExecuteSqlRawAsync(SetLockTimeoutSql, cancellationToken);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM users
            WHERE id IN ({first.Value}, {second.Value})
            ORDER BY id
            FOR UPDATE
            """, cancellationToken);

        List<User> users = await context.DomainUsers
            .Where(user => user.Id == first || user.Id == second)
            .OrderBy(user => user.Id)
            .ToListAsync(cancellationToken);

        if (users.Count != 2)
        {
            return null;
        }

        return new LockedFriendPair(users[0], users[1]);
    }
}
