using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class FriendPairLocker(AppDbContext context) : IFriendPairLocker
{
    public async Task<LockedFriendPair?> LockAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        if (first == second)
        {
            return null;
        }

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
