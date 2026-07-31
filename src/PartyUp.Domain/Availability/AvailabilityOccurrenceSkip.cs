using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>
/// A single suppressed occurrence of a weekly series (D-025/D-134). The key is
/// (series, occurrence start LocalDate in the series' zone) — DOMAIN_MODEL §8.
/// </summary>
public sealed class AvailabilityOccurrenceSkip
{
    private AvailabilityOccurrenceSkip()
    {
    }

    private AvailabilityOccurrenceSkip(AvailabilitySeriesId seriesId, LocalDate occurrenceDate)
    {
        SeriesId = seriesId;
        OccurrenceDate = occurrenceDate;
    }

    public AvailabilitySeriesId SeriesId { get; private set; }

    public LocalDate OccurrenceDate { get; private set; }

    public static AvailabilityOccurrenceSkip For(AvailabilitySeries series, LocalDate occurrenceDate)
    {
        ArgumentNullException.ThrowIfNull(series);
        series.EnsureOccurrenceDate(occurrenceDate);

        return new AvailabilityOccurrenceSkip(series.Id, occurrenceDate);
    }

    public override string ToString() =>
        FormattableString.Invariant($"skip {SeriesId} on {OccurrenceDate:yyyy-MM-dd}");
}
