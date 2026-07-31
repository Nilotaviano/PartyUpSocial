using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence.Configurations;

namespace PartyUp.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AuthUser, AuthRole, Guid>(options)
{
    public DbSet<User> DomainUsers => Set<User>();

    public DbSet<AuthUser> AuthUsers => Set<AuthUser>();

    public DbSet<PlatformLink> PlatformLinks => Set<PlatformLink>();

    public DbSet<UsernameRegistryEntry> UsernameRegistry => Set<UsernameRegistryEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new AuthUserConfiguration());
        builder.ApplyConfiguration(new UserConfiguration());
        builder.ApplyConfiguration(new PlatformLinkConfiguration());
        builder.ApplyConfiguration(new UsernameRegistryEntryConfiguration());

        builder.Entity<AuthRole>().ToTable("auth_roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("auth_user_roles");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("auth_user_logins");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("auth_user_claims");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("auth_role_claims");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("auth_user_tokens");
        builder.Ignore<IdentityUserPasskey<Guid>>();
    }
}
