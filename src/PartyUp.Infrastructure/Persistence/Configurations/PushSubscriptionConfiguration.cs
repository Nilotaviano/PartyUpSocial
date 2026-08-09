using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

// PushSubscription (src/PartyUp.Domain/Notifications/PushSubscription.cs) is shape-only and carries no
// Channel member -- only WebPush exists today, so "channel" is an adapter-owned shadow property here,
// written as a constant by PushSubscriptionRepository, on the NotificationConfiguration.ReminderHandoffId
// shadow precedent. "endpoint_hash" is a second shadow property (SHA-256 hex of Endpoint) the same
// adapter computes and writes. PHASE7_DESIGN.md §1's A-015 posture keeps Endpoint/AuthSecret themselves
// plaintext by necessity (replayed verbatim on every send); the compensations are never logging them,
// never returning them from a read contract, and purging them with the user (§11 step 5).
public sealed class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
    public void Configure(EntityTypeBuilder<PushSubscription> builder)
    {
        builder.ToTable("push_subscriptions");
        builder.HasKey(subscription => subscription.Id);
        builder.Property(subscription => subscription.Id)
            .HasConversion(new TypedIdConverter<PushSubscriptionId>())
            .ValueGeneratedNever();
        builder.Property(subscription => subscription.UserId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(subscription => subscription.Endpoint)
            .HasMaxLength(PushSubscription.MaximumEndpointLength);
        builder.Property(subscription => subscription.PublicKey)
            .HasMaxLength(PushSubscription.MaximumKeyLength);
        builder.Property(subscription => subscription.AuthSecret)
            .HasMaxLength(PushSubscription.MaximumKeyLength);
        builder.Property(subscription => subscription.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(subscription => subscription.RevokedAt).HasColumnType("timestamp with time zone");
        builder.Ignore(subscription => subscription.IsActive);

        builder.Property<short>("Channel").HasColumnName("channel").HasDefaultValue((short)0);
        builder.Property<string>("EndpointHash").HasColumnName("endpoint_hash").HasMaxLength(64).IsRequired();

        builder.HasIndex("UserId", "Channel", "EndpointHash")
            .IsUnique()
            .HasFilter("revoked_at IS NULL")
            .HasDatabaseName("ux_push_subscriptions_active_endpoint");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(subscription => subscription.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
