using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class BlockConfiguration : IEntityTypeConfiguration<Block>
{
    public void Configure(EntityTypeBuilder<Block> builder)
    {
        builder.ToTable("blocks", table =>
            table.HasCheckConstraint("ck_blocks_not_self", "blocker_id <> blocked_id"));
        builder.HasKey(block => block.Id);

        builder.Property(block => block.Id)
            .HasConversion(new TypedIdConverter<BlockId>())
            .ValueGeneratedNever();
        builder.Property(block => block.BlockerId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(block => block.BlockedId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(block => block.CreatedAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(block => block.BlockedUsernameSnapshot)
            .HasConversion(new TextValueConverter<Username>())
            .HasColumnName("blocked_username_snapshot")
            .HasMaxLength(Username.MaxLength)
            .IsRequired();

        builder.HasAlternateKey(block => new { block.BlockerId, block.BlockedId })
            .HasName("ux_blocks_ordered_pair");
        builder.HasIndex(block => new { block.BlockerId, block.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_blocks_blocker_created");
        builder.HasIndex(block => block.BlockedId)
            .HasDatabaseName("ix_blocks_blocked");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(block => block.BlockerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(block => block.BlockedId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
