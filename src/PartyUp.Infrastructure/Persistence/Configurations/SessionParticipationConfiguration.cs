using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class SessionParticipationConfiguration : IEntityTypeConfiguration<Participation>
{
    public void Configure(EntityTypeBuilder<Participation> builder)
    {
        builder.ToTable("session_participations", table =>
        {
            table.HasCheckConstraint("ck_session_participations_participant", "(user_id IS NOT NULL) <> (guest_identity_id IS NOT NULL)");
            table.HasCheckConstraint("ck_session_participations_reminder", "reminder_choice IS NULL OR user_id IS NOT NULL");
        });
        builder.HasKey(participation => participation.Id);
        builder.Property(participation => participation.Id).HasConversion(new TypedIdConverter<ParticipationId>()).ValueGeneratedNever();
        builder.Property(participation => participation.SessionId).HasConversion(new TypedIdConverter<SessionId>());
        builder.Property(participation => participation.UserId).HasConversion(new TypedIdConverter<UserId>());
        builder.Property(participation => participation.GuestIdentityId).HasConversion(new TypedIdConverter<GuestIdentityId>());
        builder.Property(participation => participation.Status).HasConversion<short>().HasColumnType("smallint");
        builder.Property(participation => participation.Origin).HasConversion<short>().HasColumnType("smallint");
        builder.Property(participation => participation.ReminderChoice).HasConversion<short>().HasColumnType("smallint");
        builder.Property(participation => participation.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(participation => participation.RespondedAt).HasColumnType("timestamp with time zone");
        builder.Ignore(participation => participation.Participant);
        builder.Ignore(participation => participation.IsRegistered);
        builder.Ignore(participation => participation.IsGuest);
        builder.Ignore(participation => participation.IsRemoved);
        builder.Ignore(participation => participation.IsActiveConsumer);
        builder.Ignore(participation => participation.IsActiveGoing);
        builder.Ignore(participation => participation.IsGoingWaitlisted);
        builder.HasIndex(participation => new { participation.SessionId, participation.UserId }).IsUnique().HasFilter("user_id IS NOT NULL").HasDatabaseName("ux_session_participations_session_user");
        builder.HasIndex(participation => new { participation.SessionId, participation.GuestIdentityId }).IsUnique().HasFilter("guest_identity_id IS NOT NULL").HasDatabaseName("ux_session_participations_session_guest");
        builder.HasIndex(participation => new { participation.UserId, participation.Status, participation.SessionId }).HasDatabaseName("ix_session_participations_user_status_session");
        builder.HasIndex(participation => new { participation.SessionId, participation.Status, participation.IsWaitlisted }).HasDatabaseName("ix_session_participations_session_status_waitlist");
        builder.HasOne<User>().WithMany().HasForeignKey(participation => participation.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GuestIdentity>().WithMany().HasForeignKey(participation => participation.GuestIdentityId).OnDelete(DeleteBehavior.Restrict);
    }
}
