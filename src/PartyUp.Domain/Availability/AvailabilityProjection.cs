using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>
/// The D-101 read-model projection for one owner: one-time blocks plus expanded series occurrences,
/// merged into continuous intervals. This is the overlap engine's input.
/// </summary>
public static class AvailabilityProjection
{
    public static IReadOnlyList<TimeRange> Project(
        IEnumerable<AvailabilityBlock> blocks,
        IEnumerable<AvailabilitySeries> series,
        IEnumerable<AvailabilityOccurrenceSkip> skips,
        TimeRange window,
        IDateTimeZoneProvider zoneProvider,
        DateTimeZone ownerZone)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(skips);

        List<AvailabilityOccurrenceSkip> allSkips = [.. skips];
        List<TimeRange> ranges = [];

        foreach (AvailabilityBlock block in blocks)
        {
            if (block.Range.Overlaps(window))
            {
                ranges.Add(block.Range);
            }
        }

        foreach (AvailabilitySeries weekly in series)
        {
            IEnumerable<LocalDate> skipped = allSkips
                .Where(skip => skip.SeriesId == weekly.Id)
                .Select(skip => skip.OccurrenceDate);

            foreach (AvailabilityOccurrence occurrence in
                AvailabilityExpansion.Expand(weekly, skipped, window, zoneProvider, ownerZone))
            {
                ranges.Add(occurrence.Range);
            }
        }

        return AvailabilityMerge.Merge(ranges);
    }
}
