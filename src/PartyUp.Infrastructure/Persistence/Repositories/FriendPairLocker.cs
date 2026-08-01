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

        List<User> users = await context.DomainUsers
            .FromSqlInterpolated($"""
                SELECT *
                FROM users
                WHERE id IN ({first.Value}, {second.Value})
                ORDER BY id
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        if (users.Count != 2)
        {
            return null;
        }

        return new LockedFriendPair(users[0], users[1]);
    }
}
