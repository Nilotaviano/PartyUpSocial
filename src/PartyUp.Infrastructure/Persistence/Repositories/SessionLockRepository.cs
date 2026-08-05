using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The bulk multi-row lock adapter A-068 declares (PHASE4_DESIGN.md §3, §6.1 step 2): one statement
/// per table, carrying the table's declared <c>ORDER BY</c>, so Postgres — never caller-computed C#
/// order — decides acquisition order. Mirrors the single-row idiom of
/// <see cref="Repositories.FriendPairLocker"/> and <c>UserRepository.FindForUpdateAsync</c>, widened to
/// an arbitrary row set.
/// </summary>
public sealed class SessionLockRepository(AppDbContext context) : ISessionLockRepository
{
    public async Task LockUsersAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return;
        }

        Guid[] ids = [.. userIds.Select(id => id.Value)];
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM users
            WHERE id = ANY({ids})
            ORDER BY id
            FOR NO KEY UPDATE
            """, cancellationToken);
    }

    public async Task LockSessionsAsync(IReadOnlyCollection<SessionId> sessionIds, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
        {
            return;
        }

        Guid[] ids = [.. sessionIds.Select(id => id.Value)];
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM sessions
            WHERE id = ANY({ids})
            ORDER BY id
            FOR NO KEY UPDATE
            """, cancellationToken);
    }
}
