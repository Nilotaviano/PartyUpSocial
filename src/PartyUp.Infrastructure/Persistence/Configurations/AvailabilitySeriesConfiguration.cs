using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

/// <summary>
/// PHASE5_DESIGN.md §4.1. <c>zone_id</c> is stored as the raw tzdb text, never through
/// <see cref="DateTimeZoneConverter"/> — that converter resolves eagerly and throws on an id that has
/// left the tzdb, which would break §12-29's "still loads and expands via the owner's current zone"
/// requirement. No <c>xmin</c> token: A-075 makes the owner/series lock the serialization point.
/// </summary>
public sealed class AvailabilitySeriesConfiguration : IEntityTypeConfiguration<AvailabilitySeries>
{
    public void Configure(EntityTypeBuilder<AvailabilitySeries> builder)
    {
        builder.ToTable("availability_series", table =>
        {
            table.HasCheckConstraint(
                "ck_availability_series_effective_range",
                "effective_until IS NULL OR effective_until >= effective_from");
            table.HasCheckConstraint(
                "ck_availability_series_grid",
                "extract(second from start_time) = 0 AND extract(minute from start_time)::int % 15 = 0");
            table.HasCheckConstraint(
                "ck_availability_series_duration",
                "duration_minutes BETWEEN 15 AND 1440 AND duration_minutes % 15 = 0");
        });

        builder.HasKey(series => series.Id);
        builder.Property(series => series.Id)
            .HasConversion(new TypedIdConverter<AvailabilitySeriesId>())
            .ValueGeneratedNever();
        builder.Property(series => series.OwnerId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(series => series.DayOfWeek)
            .HasConversion<short>()
            .HasColumnName("day_of_week")
            .HasColumnType("smallint");
        builder.Property(series => series.StartTime)
            .HasColumnName("start_time")
            .HasColumnType("time without time zone");
        builder.Property(series => series.Duration)
            .HasConversion(period => (int)period.Minutes, minutes => Period.FromMinutes(minutes))
            .HasColumnName("duration_minutes")
            .HasColumnType("integer");
        builder.Property(series => series.ZoneId)
            .HasColumnName("zone_id")
            .IsRequired();
        builder.Property(series => series.EffectiveFrom)
            .HasColumnName("effective_from")
            .HasColumnType("date");
        builder.Property(series => series.EffectiveUntil)
            .HasColumnName("effective_until")
            .HasColumnType("date");
        builder.Property<Instant>("CreatedAt")
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        builder.Ignore(series => series.Length);
        builder.Ignore(series => series.Pattern);
        builder.Ignore(series => series.IsEmpty);

        builder.HasIndex(series => new { series.OwnerId, series.EffectiveFrom, series.EffectiveUntil })
            .HasDatabaseName("ix_availability_series_owner_effective");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(series => series.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
