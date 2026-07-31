using Microsoft.EntityFrameworkCore;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Configurations;

namespace PartyUp.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<PlatformLink> PlatformLinks => Set<PlatformLink>();

    public DbSet<UsernameRegistryEntry> UsernameRegistry => Set<UsernameRegistryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new PlatformLinkConfiguration());
        modelBuilder.ApplyConfiguration(new UsernameRegistryEntryConfiguration());
    }
}
