using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The bulk multi-row lock adapter for poll commands (P3-LK-01), mirroring
/// <see cref="SessionLockRepository"/>: one statement per table, carrying the table's declared
/// <c>ORDER BY</c>, so Postgres — never caller-computed C# order — decides acquisition order.
/// </summary>
public sealed class PollLockRepository(AppDbContext context) : IPollLockRepository
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

    public async Task LockPollsAsync(IReadOnlyCollection<PollId> pollIds, CancellationToken cancellationToken)
    {
        if (pollIds.Count == 0)
        {
            return;
        }

        Guid[] ids = [.. pollIds.Select(id => id.Value)];
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM polls
            WHERE id = ANY({ids})
            ORDER BY id
            FOR NO KEY UPDATE
            """, cancellationToken);
    }
}
