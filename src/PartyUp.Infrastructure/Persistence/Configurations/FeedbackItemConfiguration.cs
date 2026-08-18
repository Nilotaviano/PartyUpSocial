using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Feedback;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

// D-214's feedback_items table. The submitter FK is SetNull-free on purpose: D-216 anonymizes
// explicitly inside the deletion executor's pass so the retention is a deliberate, tested step
// rather than a database side effect, and the FK stays Restrict like every other user reference.
public sealed class FeedbackItemConfiguration : IEntityTypeConfiguration<FeedbackItem>
{
    public void Configure(EntityTypeBuilder<FeedbackItem> builder)
    {
        builder.ToTable("feedback_items");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id)
            .HasConversion(new TypedIdConverter<FeedbackItemId>())
            .ValueGeneratedNever();
        builder.Property(item => item.SubmitterUserId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(item => item.Type).HasConversion<short>();
        builder.Property(item => item.Message)
            .HasMaxLength(FeedbackItem.MaximumMessageLength)
            .IsRequired();
        builder.Property(item => item.Route).HasMaxLength(FeedbackItem.MaximumShortContextLength);
        builder.Property(item => item.UserAgent).HasMaxLength(FeedbackItem.MaximumUserAgentLength);
        builder.Property(item => item.Locale).HasMaxLength(FeedbackItem.MaximumShortContextLength);
        builder.Property(item => item.AppVersion).HasMaxLength(FeedbackItem.MaximumShortContextLength);
        builder.Property(item => item.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(item => item.Handled).HasDefaultValue(false);
        builder.Property(item => item.HandledAt).HasColumnType("timestamp with time zone");

        // Newest-first paging (D-214): a plain btree scans backwards, so no descending declaration.
        builder.HasIndex(item => item.CreatedAt)
            .HasDatabaseName("ix_feedback_items_created_at");
        builder.HasIndex(item => item.SubmitterUserId)
            .HasDatabaseName("ix_feedback_items_submitter_user_id");
        builder.HasIndex(item => item.Handled)
            .HasFilter("handled = false")
            .HasDatabaseName("ix_feedback_items_unhandled");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(item => item.SubmitterUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
