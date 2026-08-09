using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class NotificationMuteConfiguration : IEntityTypeConfiguration<NotificationMute>
{
    public void Configure(EntityTypeBuilder<NotificationMute> builder)
    {
        builder.ToTable("notification_mutes", table => table.HasCheckConstraint("ck_notification_mutes_target", "(session_id IS NOT NULL) <> (poll_id IS NOT NULL)"));
        builder.HasKey(mute => mute.Id);
        builder.Property(mute => mute.Id).HasConversion(new TypedIdConverter<NotificationMuteId>()).ValueGeneratedNever();
        builder.Property(mute => mute.UserId).HasConversion(new TypedIdConverter<UserId>());
        builder.Property(mute => mute.SessionId).HasConversion(new TypedIdConverter<SessionId>());
        builder.Property(mute => mute.PollId).HasConversion(new TypedIdConverter<PollId>());
        builder.Property(mute => mute.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Ignore(mute => mute.TargetsSession);
        builder.Ignore(mute => mute.TargetsPoll);
        builder.HasIndex(mute => new { mute.SessionId, mute.UserId }).IsUnique().HasFilter("session_id IS NOT NULL").HasDatabaseName("ux_notification_mutes_session_user");
        // The poll-side twin (PHASE7_DESIGN.md §1): completes the xor target's logical uniqueness in
        // the DB the same way the session-side unique above already does.
        builder.HasIndex(mute => new { mute.PollId, mute.UserId }).IsUnique().HasFilter("poll_id IS NOT NULL").HasDatabaseName("ux_notification_mutes_poll_user");
        builder.HasOne<User>().WithMany().HasForeignKey(mute => mute.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
