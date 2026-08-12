using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class AvatarSourceConfiguration : IEntityTypeConfiguration<AvatarSource>
{
    public void Configure(EntityTypeBuilder<AvatarSource> builder)
    {
        builder.ToTable("avatar_sources");
        builder.HasKey(source => source.Id);

        builder.Property(source => source.Id)
            .HasConversion(new TypedIdConverter<AvatarSourceId>())
            .ValueGeneratedNever();
        builder.Property(source => source.UserId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(source => source.Kind).IsRequired();
        builder.Property(source => source.Token).IsRequired();
        builder.Property(source => source.Bytes).IsRequired();
        builder.Property(source => source.ContentType).IsRequired();
        builder.Property(source => source.FetchedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(source => source.Token)
            .IsUnique()
            .HasDatabaseName("ux_avatar_sources_token");
        builder.HasIndex(source => new { source.UserId, source.Kind })
            .IsUnique()
            .HasDatabaseName("ux_avatar_sources_user_id_kind");

        builder.HasOne<User>()
            .WithMany(user => user.AvatarSources)
            .HasForeignKey(source => source.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
