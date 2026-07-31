using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class UsernameRegistryRepository(AppDbContext context)
    : IUsernameRegistryRepository
{
    public Task<UsernameRegistryEntry?> FindAsync(
        string canonical,
        CancellationToken cancellationToken) =>
        context.UsernameRegistry
            .FromSqlInterpolated(
                $"SELECT * FROM username_registry WHERE name_canonical = {canonical} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<UsernameRegistryEntry?> FindActiveForOwnerAsync(
        UserId owner,
        CancellationToken cancellationToken) =>
        context.UsernameRegistry.SingleOrDefaultAsync(
            entry => entry.State == UsernameRegistryState.Active && entry.OwnerId == owner,
            cancellationToken);

    public void Add(UsernameRegistryEntry entry) => context.UsernameRegistry.Add(entry);
}
