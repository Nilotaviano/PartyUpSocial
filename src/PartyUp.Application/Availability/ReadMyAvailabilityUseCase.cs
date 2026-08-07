using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Availability;

/// <summary>
/// PHASE5_DESIGN.md §5.1/§5.2: the one Application composition behind both <c>GET /me/availability</c>
/// and <c>GET /calendar</c>'s <c>myAvailability</c> layer (<see cref="GetCalendarViewUseCase"/> calls
/// this rather than re-deriving). One <see cref="IAvailabilityReadRepository.ReadOwnersWindowAsync"/>
/// call for the single owner; <see cref="AvailabilityExpansion.Expand"/> supplies the addressable
/// <c>occurrences</c> form (edit surface) and <see cref="AvailabilityProjection.Project"/> the
/// <c>merged</c> ranges the week grid paints — D-101's two forms, neither re-derived from the other
/// (A-076: no Domain change, adapter composition only).
/// </summary>
public sealed class ReadMyAvailabilityUseCase(
    IAvailabilityReadRepository availabilityReads,
    IDateTimeZoneProvider zoneProvider)
{
    public async Task<MyAvailabilityLayer> ExecuteAsync(
        UserId ownerId, TimeRange range, CancellationToken cancellationToken)
    {
        OverlapEngine.EnsureRangeAllowed(range);

        IReadOnlyList<AvailabilityOwnerData> ownerData =
            await availabilityReads.ReadOwnersWindowAsync([ownerId], range, cancellationToken);
        AvailabilityOwnerData? data = ownerData.FirstOrDefault(d => d.OwnerId == ownerId);
        if (data is null)
        {
            return new MyAvailabilityLayer([], [], range);
        }

        List<MyAvailabilityOccurrence> occurrences = [];
        foreach (AvailabilityBlock block in data.Blocks)
        {
            if (block.Range.Overlaps(range))
            {
                occurrences.Add(new MyAvailabilityOccurrence(
                    block.Range, new AvailabilityOccurrenceOrigin.FromBlock(block.Id)));
            }
        }

        foreach (AvailabilitySeries series in data.Series)
        {
            IEnumerable<LocalDate> skippedDates = data.Skips
                .Where(skip => skip.SeriesId == series.Id)
                .Select(skip => skip.OccurrenceDate);

            foreach (AvailabilityOccurrence occurrence in
                AvailabilityExpansion.Expand(series, skippedDates, range, zoneProvider, data.ZoneId))
            {
                occurrences.Add(new MyAvailabilityOccurrence(
                    occurrence.Range,
                    new AvailabilityOccurrenceOrigin.FromSeriesOccurrence(occurrence.SeriesId, occurrence.OccurrenceDate)));
            }
        }

        IReadOnlyList<TimeRange> merged = AvailabilityProjection.Project(
            data.Blocks, data.Series, data.Skips, range, zoneProvider, data.ZoneId);

        return new MyAvailabilityLayer(occurrences, merged, range);
    }
}
