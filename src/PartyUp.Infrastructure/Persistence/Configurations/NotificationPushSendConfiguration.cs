using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Infrastructure.Notifications;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class NotificationPushSendConfiguration : IEntityTypeConfiguration<NotificationPushSend>
{
    public void Configure(EntityTypeBuilder<NotificationPushSend> builder)
    {
        builder.ToTable("notification_push_sends");
        builder.HasKey(send => send.Id);
        builder.Property(send => send.Id).ValueGeneratedNever();
        builder.Property(send => send.NotificationId).HasConversion(new TypedIdConverter<NotificationId>());
        builder.Property(send => send.PushSubscriptionId).HasConversion(new TypedIdConverter<PushSubscriptionId>());
        builder.Property(send => send.ClaimedAt).HasColumnType("timestamp with time zone");
        builder.Property(send => send.Outcome).HasConversion<short>().HasColumnType("smallint");

        builder.HasIndex(send => new { send.NotificationId, send.PushSubscriptionId })
            .IsUnique()
            .HasDatabaseName("ux_notification_push_sends_once");

        // The FK the § 1 "removed via subscription FK scope" deletion purge relies on: deleting a
        // user's push_subscriptions rows (IPushSubscriptionRepository.DeleteForUserAsync) cascades
        // their send-log rows without a second delete statement.
        builder.HasOne<PushSubscription>()
            .WithMany()
            .HasForeignKey(send => send.PushSubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Notification>()
            .WithMany()
            .HasForeignKey(send => send.NotificationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
