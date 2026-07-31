using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class UserRepository(AppDbContext context) : IUserRepository
{
    public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken) =>
        context.DomainUsers.SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

    public Task<User?> FindForUpdateAsync(UserId id, CancellationToken cancellationToken) =>
        context.DomainUsers
            .FromSqlInterpolated($"SELECT * FROM users WHERE id = {id.Value} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
        Instant now,
        int max,
        CancellationToken cancellationToken) =>
        await context.DomainUsers
            .AsNoTracking()
            .Where(user =>
                user.Lifecycle == UserLifecycle.PendingDeletion &&
                user.GraceEndsAt != null &&
                user.GraceEndsAt <= now)
            .OrderBy(user => user.GraceEndsAt)
            .Select(user => user.Id)
            .Take(max)
            .ToListAsync(cancellationToken);

    public void Add(User user) => context.DomainUsers.Add(user);
}
