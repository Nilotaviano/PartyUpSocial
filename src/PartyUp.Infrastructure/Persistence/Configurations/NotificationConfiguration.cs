using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

// This adapter type has no Domain footprint by design: `Notification` (`src/PartyUp.Domain/Notifications/Notification.cs`)
// carries no `ReminderHandoffId` member, so the reminder link is declared as an EF shadow property here and is
// written only by Infrastructure, never surfaced through a read model (PHASE4_DESIGN.md §4.1).

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications", table =>
        {
            table.HasCheckConstraint(
                "ck_notifications_type",
                "type BETWEEN 0 AND 24");
            table.HasCheckConstraint(
                "ck_notifications_friend_shape",
                "type NOT IN (23, 24) OR " +
                "(actor_user_id IS NOT NULL AND session_id IS NULL AND poll_id IS NULL " +
                "AND option_start_time IS NULL)");
        });
        builder.HasKey(notification => notification.Id);

        builder.Property(notification => notification.Id)
            .HasConversion(new TypedIdConverter<NotificationId>())
            .ValueGeneratedNever();
        builder.Property(notification => notification.RecipientUserId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(notification => notification.Type)
            .HasConversion<short>()
            .HasColumnType("smallint");
        builder.Property(notification => notification.SessionId)
            .HasConversion(new TypedIdConverter<SessionId>());
        builder.Property(notification => notification.PollId)
            .HasConversion(new TypedIdConverter<PollId>());
        builder.Property(notification => notification.ActorUserId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(notification => notification.OptionStartTime)
            .HasColumnType("timestamp with time zone");
        builder.Property(notification => notification.CreatedAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(notification => notification.ReadAt)
            .HasColumnType("timestamp with time zone");
        builder.Property<Guid?>("ReminderHandoffId")
            .HasColumnName("reminder_handoff_id");

        builder.Ignore(notification => notification.IsRead);
        builder.Ignore(notification => notification.Subject);

        builder.HasIndex(notification => new
            {
                notification.RecipientUserId,
                notification.CreatedAt,
                notification.Id,
            })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_notifications_recipient_created");
        builder.HasIndex(notification => new
            {
                notification.RecipientUserId,
                notification.ActorUserId,
            })
            .IsUnique()
            .HasFilter("type = 24")
            .HasDatabaseName("ux_notifications_steam_match_once");
        builder.HasIndex("ReminderHandoffId")
            .IsUnique()
            .HasFilter("type = 12")
            .HasDatabaseName("ux_notifications_session_reminder_once");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(notification => notification.RecipientUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(notification => notification.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SessionReminderHandoff>()
            .WithMany()
            .HasForeignKey("ReminderHandoffId")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
