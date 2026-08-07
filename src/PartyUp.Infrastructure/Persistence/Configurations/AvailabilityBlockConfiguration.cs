using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

/// <summary>
/// PHASE5_DESIGN.md §4.1. <c>users → availability_blocks</c> is RESTRICT (users are tombstoned, never
/// hard-deleted, §11 step 6); <c>availability_series → availability_blocks</c> is CASCADE — a
/// materialized block's provenance dies with the series that produced it (§12-21). No <c>xmin</c>
/// token: A-075 makes the owner/series lock the serialization point.
/// </summary>
public sealed class AvailabilityBlockConfiguration : IEntityTypeConfiguration<AvailabilityBlock>
{
    public void Configure(EntityTypeBuilder<AvailabilityBlock> builder)
    {
        builder.ToTable("availability_blocks", table =>
        {
            table.HasCheckConstraint("ck_availability_blocks_range", "end_time > start_time");
            table.HasCheckConstraint(
                "ck_availability_blocks_grid",
                "date_trunc('minute', start_time) = start_time AND date_trunc('minute', end_time) = end_time "
                + "AND extract(minute from start_time)::int % 15 = 0 AND extract(minute from end_time)::int % 15 = 0");
            table.HasCheckConstraint(
                "ck_availability_blocks_provenance",
                "(source_series_id IS NULL) = (source_occurrence_date IS NULL)");
        });

        builder.HasKey(block => block.Id);
        builder.Property(block => block.Id)
            .HasConversion(new TypedIdConverter<AvailabilityBlockId>())
            .ValueGeneratedNever();
        builder.Property(block => block.OwnerId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(block => block.Start)
            .HasColumnName("start_time")
            .HasColumnType("timestamp with time zone");
        builder.Property(block => block.End)
            .HasColumnName("end_time")
            .HasColumnType("timestamp with time zone");
        builder.Property(block => block.SourceSeriesId)
            .HasConversion(new TypedIdConverter<AvailabilitySeriesId>())
            .HasColumnName("source_series_id");
        builder.Property(block => block.SourceOccurrenceDate)
            .HasColumnName("source_occurrence_date")
            .HasColumnType("date");
        builder.Property<Instant>("CreatedAt")
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        builder.Ignore(block => block.Range);
        builder.Ignore(block => block.IsMaterializedFromSeries);

        builder.HasIndex(block => new { block.OwnerId, block.Start, block.End })
            .HasDatabaseName("ix_availability_blocks_owner_start_end");
        builder.HasIndex(block => new { block.SourceSeriesId, block.SourceOccurrenceDate })
            .HasDatabaseName("ix_availability_blocks_source_series_occurrence");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(block => block.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AvailabilitySeries>()
            .WithMany()
            .HasForeignKey(block => block.SourceSeriesId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
