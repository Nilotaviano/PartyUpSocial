using NodaTime;
using NodaTime.TimeZones;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>
/// Turns a weekly series into absolute occurrences (DOMAIN_MODEL §8, §12-19).
/// Endpoints are wall-clock: <c>s = date.At(StartTime)</c> and <c>e = s + Duration</c> are
/// LocalDateTime arithmetic, then both are mapped through the zone — so a block spanning a fall-back
/// is longer in real time, one spanning a spring-forward is shorter, and one swallowed entirely by a
/// DST gap collapses and is dropped.
/// </summary>
public static class AvailabilityExpansion
{
    /// <summary>
    /// Ambiguous wall-clock times take the earlier offset; skipped ones clamp to the end of the gap,
    /// which is what makes a gap-swallowed occurrence collapse to zero length (§12-19).
    /// NodaTime's own <c>AtLeniently</c> forward-shifts skipped times instead, preserving length —
    /// that would silently invent availability at a wall-clock time the owner never declared.
    /// </summary>
    private static readonly ZoneLocalMappingResolver Resolver =
        Resolvers.CreateMappingResolver(Resolvers.ReturnEarlier, Resolvers.ReturnStartOfIntervalAfter);

    /// <summary>§12-29: an id that has left the tzdb expands via the owner's current zone.</summary>
    public static DateTimeZone ResolveZone(
        AvailabilitySeries series,
        IDateTimeZoneProvider zoneProvider,
        DateTimeZone ownerZone)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(zoneProvider);
        ArgumentNullException.ThrowIfNull(ownerZone);

        return zoneProvider.GetZoneOrNull(series.ZoneId) ?? ownerZone;
    }

    public static IReadOnlyList<AvailabilityOccurrence> Expand(
        AvailabilitySeries series,
        IEnumerable<LocalDate> skippedDates,
        TimeRange window,
        IDateTimeZoneProvider zoneProvider,
        DateTimeZone ownerZone) =>
        ExpandIn(series, skippedDates, window, ResolveZone(series, zoneProvider, ownerZone));

    public static IReadOnlyList<AvailabilityOccurrence> ExpandIn(
        AvailabilitySeries series,
        IEnumerable<LocalDate> skippedDates,
        TimeRange window,
        DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(skippedDates);
        ArgumentNullException.ThrowIfNull(zone);

        HashSet<LocalDate> skips = [.. skippedDates];
        Period duration = series.Duration;

        // An occurrence may start before the window and reach into it; a 24 h block plus a DST shift
        // never reaches back more than two local days.
        LocalDate scanFrom = window.Start.InZone(zone).Date.PlusDays(-2);
        LocalDate scanTo = window.End.InZone(zone).Date.PlusDays(1);

        LocalDate cursor = scanFrom > series.EffectiveFrom ? scanFrom : series.EffectiveFrom;
        if (cursor.DayOfWeek != series.DayOfWeek)
        {
            cursor = cursor.Next(series.DayOfWeek);
        }

        List<AvailabilityOccurrence> occurrences = [];
        for (; cursor <= scanTo; cursor = cursor.PlusWeeks(1))
        {
            if (series.EffectiveUntil is { } until && cursor >= until)
            {
                break;
            }

            if (skips.Contains(cursor))
            {
                continue;
            }

            LocalDateTime localStart = cursor.At(series.StartTime);
            LocalDateTime localEnd = localStart + duration;
            Instant start = zone.ResolveLocal(localStart, Resolver).ToInstant();
            Instant end = zone.ResolveLocal(localEnd, Resolver).ToInstant();

            if (end <= start)
            {
                continue;
            }

            TimeRange range = new(start, end);
            if (range.Overlaps(window))
            {
                occurrences.Add(new AvailabilityOccurrence(series.Id, cursor, range));
            }
        }

        return occurrences;
    }
}
