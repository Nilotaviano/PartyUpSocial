using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class UsernameRegistryRepository(AppDbContext context)
    : IUsernameRegistryRepository
{
    public async Task<UsernameRegistryEntry?> FindAsync(
        string canonical,
        CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM username_registry WHERE name_canonical = {canonical} FOR UPDATE",
            cancellationToken);
        return await context.UsernameRegistry.SingleOrDefaultAsync(
            entry => entry.NameCanonical == canonical,
            cancellationToken);
    }

    public Task<UsernameRegistryEntry?> FindActiveForOwnerAsync(
        UserId owner,
        CancellationToken cancellationToken) =>
        context.UsernameRegistry.SingleOrDefaultAsync(
            entry => entry.State == UsernameRegistryState.Active && entry.OwnerId == owner,
            cancellationToken);

    public void Add(UsernameRegistryEntry entry) => context.UsernameRegistry.Add(entry);
}
