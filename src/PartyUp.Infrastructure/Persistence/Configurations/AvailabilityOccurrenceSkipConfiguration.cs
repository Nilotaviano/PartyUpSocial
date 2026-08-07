using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

/// <summary>
/// PHASE5_DESIGN.md §4.1. The composite primary key <c>(series_id, occurrence_date)</c> is the
/// exception key of DOMAIN_MODEL.md:195, which makes a repeated delete-one idempotent at the database
/// boundary. No surrogate <c>id</c>, no <c>xmin</c> token.
/// </summary>
public sealed class AvailabilityOccurrenceSkipConfiguration : IEntityTypeConfiguration<AvailabilityOccurrenceSkip>
{
    public void Configure(EntityTypeBuilder<AvailabilityOccurrenceSkip> builder)
    {
        builder.ToTable("availability_occurrence_skips");

        builder.HasKey(skip => new { skip.SeriesId, skip.OccurrenceDate })
            .HasName("pk_availability_occurrence_skips");

        builder.Property(skip => skip.SeriesId)
            .HasConversion(new TypedIdConverter<AvailabilitySeriesId>())
            .HasColumnName("series_id");
        builder.Property(skip => skip.OccurrenceDate)
            .HasColumnName("occurrence_date")
            .HasColumnType("date");
        builder.Property<Instant>("CreatedAt")
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        builder.HasOne<AvailabilitySeries>()
            .WithMany()
            .HasForeignKey(skip => skip.SeriesId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
