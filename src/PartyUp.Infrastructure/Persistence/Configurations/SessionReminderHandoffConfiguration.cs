using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class SessionReminderHandoffConfiguration : IEntityTypeConfiguration<SessionReminderHandoff>
{
    public void Configure(EntityTypeBuilder<SessionReminderHandoff> builder)
    {
        builder.ToTable("session_reminder_handoffs", table => table.HasCheckConstraint("ck_session_reminder_handoffs_delivered", "(state = 2) = (delivered_at IS NOT NULL)"));
        builder.HasKey(handoff => handoff.Id);
        builder.Property(handoff => handoff.Id).ValueGeneratedNever();
        builder.Property(handoff => handoff.SessionId).HasConversion(new TypedIdConverter<SessionId>());
        builder.Property(handoff => handoff.RecipientUserId).HasConversion(new TypedIdConverter<UserId>());
        builder.Property(handoff => handoff.DueAt).HasColumnType("timestamp with time zone");
        builder.Property(handoff => handoff.State).HasConversion<short>().HasColumnType("smallint");
        builder.Property(handoff => handoff.PlannedAt).HasColumnType("timestamp with time zone");
        builder.Property(handoff => handoff.DeliveredAt).HasColumnType("timestamp with time zone");
        builder.Property(handoff => handoff.Attempts).HasDefaultValue(0);
        builder.Property(handoff => handoff.LastAttemptAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(handoff => new { handoff.SessionId, handoff.RecipientUserId }).IsUnique().HasDatabaseName("ux_session_reminder_handoffs_session_recipient");
        builder.HasIndex(handoff => new { handoff.State, handoff.DueAt, handoff.Id }).HasDatabaseName("ix_session_reminder_handoffs_state_due_id");
        builder.HasOne<Session>().WithMany().HasForeignKey(handoff => handoff.SessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(handoff => handoff.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
