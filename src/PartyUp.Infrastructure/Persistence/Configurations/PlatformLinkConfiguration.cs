using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class PlatformLinkConfiguration : IEntityTypeConfiguration<PlatformLink>
{
    public void Configure(EntityTypeBuilder<PlatformLink> builder)
    {
        builder.ToTable("platform_links");
        builder.HasKey(link => link.Id);

        builder.Property(link => link.Id)
            .HasConversion(new TypedIdConverter<PlatformLinkId>())
            .ValueGeneratedNever();
        builder.Property(link => link.UserId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(link => link.Platform).IsRequired();
        builder.Property(link => link.Handle)
            .HasConversion(new TextValueConverter<GamingHandle>())
            .IsRequired();

        builder.HasIndex(link => new { link.UserId, link.Platform })
            .IsUnique()
            .HasDatabaseName("ux_platform_links_user_id_platform");

        builder.HasOne<User>()
            .WithMany(user => user.PlatformLinks)
            .HasForeignKey(link => link.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
