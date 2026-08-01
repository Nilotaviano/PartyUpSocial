using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class FriendshipConfiguration : IEntityTypeConfiguration<Friendship>
{
    public void Configure(EntityTypeBuilder<Friendship> builder)
    {
        builder.ToTable("friendships", table =>
        {
            table.HasCheckConstraint(
                "ck_friendships_canonical",
                "user_lo_id < user_hi_id");
            table.HasCheckConstraint(
                "ck_friendships_requester",
                "requested_by IN (user_lo_id, user_hi_id)");
            table.HasCheckConstraint(
                "ck_friendships_status",
                "status IN (0, 1)");
            table.HasCheckConstraint(
                "ck_friendships_accept_time",
                "(status = 0 AND accepted_at IS NULL) OR " +
                "(status = 1 AND accepted_at IS NOT NULL)");
        });
        builder.HasKey(friendship => friendship.Id);
        builder.UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Property(friendship => friendship.Id)
            .HasConversion(new TypedIdConverter<FriendshipId>())
            .ValueGeneratedNever();
        builder.Property(friendship => friendship.UserLoId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(friendship => friendship.UserHiId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(friendship => friendship.RequestedBy)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(friendship => friendship.Status)
            .HasConversion<short>()
            .HasColumnType("smallint");
        builder.Property(friendship => friendship.RequestedAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(friendship => friendship.AcceptedAt)
            .HasColumnType("timestamp with time zone");

        builder.Ignore(friendship => friendship.AddresseeId);
        builder.Ignore(friendship => friendship.IsPending);
        builder.Ignore(friendship => friendship.IsAccepted);

        builder.HasAlternateKey(friendship => new { friendship.UserLoId, friendship.UserHiId })
            .HasName("ux_friendships_pair");
        builder.HasIndex(friendship => new { friendship.UserLoId, friendship.Status })
            .HasDatabaseName("ix_friendships_lo_status");
        builder.HasIndex(friendship => new { friendship.UserHiId, friendship.Status })
            .HasDatabaseName("ix_friendships_hi_status");
        builder.HasIndex(friendship => new { friendship.RequestedBy, friendship.RequestedAt })
            .IsDescending(false, true)
            .HasFilter("status = 0")
            .HasDatabaseName("ix_friendships_pending_requested_by");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(friendship => friendship.UserLoId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(friendship => friendship.UserHiId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(friendship => friendship.RequestedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property<uint>("xmin").IsRowVersion();
    }
}
