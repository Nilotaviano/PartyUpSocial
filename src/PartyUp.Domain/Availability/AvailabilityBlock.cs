using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>
/// A one-time availability block: a half-open <c>[Start, End)</c> interval on the 15-minute grid
/// (D-071/D-132). A block materialized by an edit-one occurrence edit (D-143) carries the series it
/// came from plus that occurrence's date; it is an absolute instant pair from then on and a later
/// D-061 zone change does not move it (DOMAIN_MODEL §12-21).
/// </summary>
public sealed class AvailabilityBlock : AggregateRoot
{
    private AvailabilityBlock()
    {
    }

    private AvailabilityBlock(
        AvailabilityBlockId id,
        UserId ownerId,
        TimeRange range,
        AvailabilitySeriesId? sourceSeriesId,
        LocalDate? sourceOccurrenceDate)
    {
        EnsureOnGrid(range);

        Id = id;
        OwnerId = ownerId;
        Start = range.Start;
        End = range.End;
        SourceSeriesId = sourceSeriesId;
        SourceOccurrenceDate = sourceOccurrenceDate;
    }

    public AvailabilityBlockId Id { get; private set; }

    public UserId OwnerId { get; private set; }

    public Instant Start { get; private set; }

    public Instant End { get; private set; }

    public AvailabilitySeriesId? SourceSeriesId { get; private set; }

    public LocalDate? SourceOccurrenceDate { get; private set; }

    public TimeRange Range => new(Start, End);

    public bool IsMaterializedFromSeries => SourceSeriesId.HasValue;

    public static AvailabilityBlock Create(UserId ownerId, TimeRange range) =>
        new(AvailabilityBlockId.New(), ownerId, range, null, null);

    /// <summary>The edit-one half of D-143: a concrete block standing in for one series occurrence.</summary>
    public static AvailabilityBlock Materialize(AvailabilitySeries series, LocalDate occurrenceDate, TimeRange range)
    {
        ArgumentNullException.ThrowIfNull(series);
        series.EnsureOccurrenceDate(occurrenceDate);

        return new AvailabilityBlock(AvailabilityBlockId.New(), series.OwnerId, range, series.Id, occurrenceDate);
    }

    public void Reschedule(TimeRange range)
    {
        EnsureOnGrid(range);

        Start = range.Start;
        End = range.End;
    }

    private static void EnsureOnGrid(TimeRange range)
    {
        if (!Duration15.IsOnGrid(range.Start) || !Duration15.IsOnGrid(range.End))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"Availability must fall on the {Duration15.GranularityMinutes}-minute grid, but was {range}."));
        }
    }
}
