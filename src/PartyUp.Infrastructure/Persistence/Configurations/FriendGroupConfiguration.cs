using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class FriendGroupConfiguration : IEntityTypeConfiguration<FriendGroup>
{
    /// <summary>D-199's 1..50 trimmed bound, which <c>GroupName</c> keeps private to itself.</summary>
    private const int NameMaxLength = 50;

    public void Configure(EntityTypeBuilder<FriendGroup> builder)
    {
        builder.ToTable("friend_groups");
        builder.HasKey(group => group.Id);
        builder.UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Property(group => group.Id)
            .HasConversion(new TypedIdConverter<FriendGroupId>())
            .ValueGeneratedNever();
        builder.Property(group => group.OwnerId)
            .HasConversion(new TypedIdConverter<UserId>())
            .HasColumnName("owner_user_id");
        builder.Property(group => group.Name)
            .HasConversion(name => name.Value, value => GroupName.Create(value))
            .HasMaxLength(NameMaxLength);
        builder.Property(group => group.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(group => group.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        // The unique index is on (owner_user_id, lower(name)) -- D-199's per-owner, case-insensitive
        // name space. EF has no expression-index primitive, so it is raw SQL in the migration. Its
        // (owner_user_id, ...) prefix already covers owner-only lookups, so no separate index on
        // OwnerId alone is needed.

        builder.OwnsMany(group => group.Members, member =>
        {
            member.ToTable("friend_group_members", table =>
            {
                table.HasCheckConstraint(
                    "ck_friend_group_members_status",
                    "status IN (0, 1)");
                table.HasCheckConstraint(
                    "ck_friend_group_members_accept_time",
                    "(status = 0 AND accepted_at IS NULL) OR " +
                    "(status = 1 AND accepted_at IS NOT NULL)");
            });
            member.WithOwner().HasForeignKey("GroupId");
            member.Property<FriendGroupId>("GroupId")
                .HasConversion(new TypedIdConverter<FriendGroupId>())
                .HasColumnName("group_id");
            member.Property(row => row.UserId)
                .HasConversion(new TypedIdConverter<UserId>());
            member.HasKey("GroupId", nameof(FriendGroupMember.UserId));
            member.Property(row => row.Status)
                .HasConversion<short>()
                .HasColumnType("smallint");
            member.Property(row => row.InvitedAt)
                .HasColumnType("timestamp with time zone");
            member.Property(row => row.AcceptedAt)
                .HasColumnType("timestamp with time zone");
            member.HasIndex(row => row.UserId).HasDatabaseName("ix_friend_group_members_user");
            member.HasOne<User>()
                .WithMany()
                .HasForeignKey(row => row.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Navigation(group => group.Members)
            .HasField("_members")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Property<uint>("xmin").IsRowVersion();
    }
}
