using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class PollParticipantConfiguration : IEntityTypeConfiguration<PollParticipant>
{
    public void Configure(EntityTypeBuilder<PollParticipant> builder)
    {
        builder.ToTable("poll_participants", table =>
            table.HasCheckConstraint("ck_poll_participants_participant", "(user_id IS NOT NULL) <> (guest_identity_id IS NOT NULL)"));
        builder.HasKey(participant => participant.Id);
        builder.Property(participant => participant.Id).HasConversion(new TypedIdConverter<PollParticipantId>()).ValueGeneratedNever();
        builder.Property(participant => participant.PollId).HasConversion(new TypedIdConverter<PollId>());
        builder.Property(participant => participant.UserId).HasConversion(new TypedIdConverter<UserId>());
        builder.Property(participant => participant.GuestIdentityId).HasConversion(new TypedIdConverter<GuestIdentityId>());
        builder.Property(participant => participant.Status).HasConversion<short>().HasColumnType("smallint");
        builder.Property(participant => participant.FirstResponseAt).HasColumnType("timestamp with time zone");
        builder.Property(participant => participant.CreatedAt).HasColumnType("timestamp with time zone");

        builder.Ignore(participant => participant.Participant);
        builder.Ignore(participant => participant.IsRegistered);
        builder.Ignore(participant => participant.IsGuest);
        builder.Ignore(participant => participant.IsActive);
        builder.Ignore(participant => participant.IsRemoved);

        // Two-partial-index ParticipantRef pattern (SessionParticipationConfiguration.cs:36-37).
        builder.HasIndex(participant => new { participant.PollId, participant.UserId }).IsUnique().HasFilter("user_id IS NOT NULL").HasDatabaseName("ux_poll_participants_poll_user");
        builder.HasIndex(participant => new { participant.PollId, participant.GuestIdentityId }).IsUnique().HasFilter("guest_identity_id IS NOT NULL").HasDatabaseName("ux_poll_participants_poll_guest");

        builder.HasOne<User>().WithMany().HasForeignKey(participant => participant.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GuestIdentity>().WithMany().HasForeignKey(participant => participant.GuestIdentityId).OnDelete(DeleteBehavior.Restrict);
    }
}
