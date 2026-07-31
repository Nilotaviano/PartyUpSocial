using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class SteamIdentityConfiguration : IEntityTypeConfiguration<SteamIdentity>
{
    public void Configure(EntityTypeBuilder<SteamIdentity> builder)
    {
        builder.ToTable("steam_identities");
        builder.HasKey(identity => identity.UserId);
        builder.Property(identity => identity.UserId)
            .HasConversion(new TypedIdConverter<UserId>())
            .ValueGeneratedNever();
        builder.Property(identity => identity.SteamId64).HasMaxLength(20).IsRequired();
        builder.Property(identity => identity.PersonaName).HasMaxLength(64).IsRequired();
        builder.Property(identity => identity.AvatarUrl).HasMaxLength(2048);
        builder.Property(identity => identity.FetchedAt).IsRequired();
        builder.HasIndex(identity => identity.SteamId64)
            .IsUnique()
            .HasDatabaseName("ux_steam_identities_steam_id64");
        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<SteamIdentity>(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
