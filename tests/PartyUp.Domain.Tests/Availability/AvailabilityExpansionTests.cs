using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using static PartyUp.Domain.Tests.Availability.AvailabilityTestData;

namespace PartyUp.Domain.Tests.Availability;

public class AvailabilityExpansionTests
{
    private static readonly IDateTimeZoneProvider Tzdb = DateTimeZoneProviders.Tzdb;

    /// <summary>The round-2 review walk: an ordinary Saturday 23:00 + 2 h week in Santiago.</summary>
    [Fact]
    public void Expand_OrdinaryWeek_CarriesPastMidnightWithoutWrapping_D132()
    {
        AvailabilitySeries series = SaturdayNightSeries();

        IReadOnlyList<AvailabilityOccurrence> occurrences = ExpandOver(
            series, At(2024, 3, 30, 0, 0), At(2024, 4, 1, 0, 0));

        AvailabilityOccurrence occurrence = Assert.Single(occurrences);
        Assert.Equal(new LocalDate(2024, 3, 30), occurrence.OccurrenceDate);
        Assert.Equal(At(2024, 3, 31, 2, 0), occurrence.Range.Start);
        Assert.Equal(At(2024, 3, 31, 4, 0), occurrence.Range.End);
        Assert.Equal(Duration.FromHours(2), occurrence.Range.Length);
        Assert.True(occurrence.Range.End > occurrence.Range.Start);
    }

    [Fact]
    public void Expand_FallBackWeek_IsLongerInRealTime_D061()
    {
        AvailabilitySeries series = SaturdayNightSeries();

        IReadOnlyList<AvailabilityOccurrence> occurrences = ExpandOver(
            series, At(2024, 4, 6, 0, 0), At(2024, 4, 8, 0, 0));

        AvailabilityOccurrence occurrence = Assert.Single(occurrences);
        Assert.Equal(new LocalDate(2024, 4, 6), occurrence.OccurrenceDate);
        Assert.Equal(At(2024, 4, 7, 2, 0), occurrence.Range.Start);
        Assert.Equal(At(2024, 4, 7, 5, 0), occurrence.Range.End);
        Assert.Equal(Duration.FromHours(3), occurrence.Range.Length);
    }

    [Fact]
    public void Expand_SpringForwardWeek_IsShorterInRealTime_D061()
    {
        AvailabilitySeries series = SaturdayNightSeries();

        IReadOnlyList<AvailabilityOccurrence> occurrences = ExpandOver(
            series, At(2024, 9, 7, 0, 0), At(2024, 9, 9, 0, 0));

        AvailabilityOccurrence occurrence = Assert.Single(occurrences);
        Assert.Equal(new LocalDate(2024, 9, 7), occurrence.OccurrenceDate);
        Assert.Equal(At(2024, 9, 8, 3, 0), occurrence.Range.Start);
        Assert.Equal(At(2024, 9, 8, 4, 0), occurrence.Range.End);
        Assert.Equal(Duration.FromHours(1), occurrence.Range.Length);
    }

    [Fact]
    public void Expand_KeepsTheSameLocalWallClockTimeAcrossTheTransition_D061()
    {
        AvailabilitySeries series = SaturdayNightSeries();

        IReadOnlyList<AvailabilityOccurrence> occurrences = ExpandOver(
            series, At(2024, 3, 30, 0, 0), At(2024, 4, 21, 0, 0));

        Assert.Equal(3, occurrences.Count);
        foreach (AvailabilityOccurrence occurrence in occurrences)
        {
            ZonedDateTime start = occurrence.Range.Start.InZone(Santiago);
            Assert.Equal(new LocalTime(23, 0), start.TimeOfDay);
            Assert.Equal(occurrence.OccurrenceDate, start.Date);
        }
    }

    [Fact]
    public void Expand_DropsAnOccurrenceSwallowedByADaylightSavingGap_D061()
    {
        AvailabilitySeries series = WeeklySeries(
            UserId.New(), IsoDayOfWeek.Sunday, new LocalTime(0, 0), 30, Santiago, new LocalDate(2024, 9, 1));

        IReadOnlyList<AvailabilityOccurrence> occurrences = ExpandOver(
            series, At(2024, 9, 1, 0, 0), At(2024, 9, 16, 0, 0));

        Assert.Equal(
            new[] { new LocalDate(2024, 9, 1), new LocalDate(2024, 9, 15) },
            occurrences.Select(occurrence => occurrence.OccurrenceDate));
    }

    [Fact]
    public void Expand_SkipsSuppressedOccurrences_D025()
    {
        AvailabilitySeries series = SaturdayNightSeries();

        IReadOnlyList<AvailabilityOccurrence> occurrences = AvailabilityExpansion.ExpandIn(
            series,
            [new LocalDate(2024, 4, 6)],
            new TimeRange(At(2024, 3, 30, 0, 0), At(2024, 4, 21, 0, 0)),
            Santiago);

        Assert.Equal(
            new[] { new LocalDate(2024, 3, 30), new LocalDate(2024, 4, 13) },
            occurrences.Select(occurrence => occurrence.OccurrenceDate));
    }

    [Fact]
    public void Expand_NeverReachesBeforeTheDraggedWeek_D132()
    {
        AvailabilitySeries series = WeeklySeries(
            UserId.New(), IsoDayOfWeek.Saturday, new LocalTime(23, 0), 120, Santiago, new LocalDate(2024, 4, 10));

        IReadOnlyList<AvailabilityOccurrence> occurrences = ExpandOver(
            series, At(2024, 1, 1, 0, 0), At(2024, 4, 22, 0, 0));

        Assert.Equal(
            new[] { new LocalDate(2024, 4, 13), new LocalDate(2024, 4, 20) },
            occurrences.Select(occurrence => occurrence.OccurrenceDate));
    }

    [Fact]
    public void Expand_RunsUntilDeletedWhenEffectiveUntilIsNull_D133()
    {
        AvailabilitySeries series = SaturdayNightSeries();

        IReadOnlyList<AvailabilityOccurrence> occurrences = ExpandOver(
            series, At(2030, 3, 1, 0, 0), At(2030, 4, 1, 0, 0));

        Assert.Equal(5, occurrences.Count);
    }

    [Fact]
    public void Expand_StopsAtTheExclusiveEffectiveUntil_D133()
    {
        AvailabilitySeries series = SaturdayNightSeries();
        series.TruncateAt(new LocalDate(2024, 4, 13));

        IReadOnlyList<AvailabilityOccurrence> occurrences = ExpandOver(
            series, At(2024, 3, 30, 0, 0), At(2024, 4, 21, 0, 0));

        Assert.Equal(
            new[] { new LocalDate(2024, 3, 30), new LocalDate(2024, 4, 6) },
            occurrences.Select(occurrence => occurrence.OccurrenceDate));
    }

    [Fact]
    public void Expand_IncludesAnOccurrenceThatStartsBeforeTheWindowAndReachesIntoIt_D132()
    {
        AvailabilitySeries series = SaturdayNightSeries();

        IReadOnlyList<AvailabilityOccurrence> occurrences = ExpandOver(
            series, At(2024, 3, 31, 3, 0), At(2024, 3, 31, 12, 0));

        AvailabilityOccurrence occurrence = Assert.Single(occurrences);
        Assert.Equal(new LocalDate(2024, 3, 30), occurrence.OccurrenceDate);
    }

    [Fact]
    public void Expand_ExcludesAnOccurrenceMerelyAbuttingTheWindow_D101()
    {
        AvailabilitySeries series = SaturdayNightSeries();

        IReadOnlyList<AvailabilityOccurrence> occurrences = ExpandOver(
            series, At(2024, 3, 31, 4, 0), At(2024, 3, 31, 12, 0));

        Assert.Empty(occurrences);
    }

    [Fact]
    public void ResolveZone_FallsBackToTheOwnersZoneWhenTheIdLeftTheTzdb_D061()
    {
        AvailabilitySeries series = AvailabilitySeries.Create(
            UserId.New(),
            WeeklyPattern.Create(IsoDayOfWeek.Saturday, new LocalTime(23, 0), Duration15.FromMinutes(120)),
            new RetiredZone(),
            new LocalDate(2024, 3, 30));

        Assert.Null(Tzdb.GetZoneOrNull(series.ZoneId));
        Assert.Equal(Santiago, AvailabilityExpansion.ResolveZone(series, Tzdb, Santiago));

        IReadOnlyList<AvailabilityOccurrence> occurrences = ExpandOver(
            series, At(2024, 3, 30, 0, 0), At(2024, 4, 1, 0, 0));

        Assert.Equal(At(2024, 3, 31, 2, 0), Assert.Single(occurrences).Range.Start);
    }

    [Fact]
    public void ResolveZone_UsesTheStoredZoneWhenItStillExists_D061()
    {
        AvailabilitySeries series = SaturdayNightSeries();

        Assert.Equal(Santiago, AvailabilityExpansion.ResolveZone(series, Tzdb, DateTimeZone.Utc));
    }

    [Fact]
    public void Expand_TwentyFourHourOccurrenceStillLandsInAShortWindow_D142()
    {
        AvailabilitySeries series = WeeklySeries(
            UserId.New(), IsoDayOfWeek.Monday, new LocalTime(0, 0), 1440, DateTimeZone.Utc, new LocalDate(2026, 8, 3));

        IReadOnlyList<AvailabilityOccurrence> occurrences = AvailabilityExpansion.ExpandIn(
            series, [], new TimeRange(At(2026, 8, 3, 23, 0), At(2026, 8, 3, 23, 45)), DateTimeZone.Utc);

        AvailabilityOccurrence occurrence = Assert.Single(occurrences);
        Assert.Equal(At(2026, 8, 3, 0, 0), occurrence.Range.Start);
        Assert.Equal(At(2026, 8, 4, 0, 0), occurrence.Range.End);
    }

    private static AvailabilitySeries SaturdayNightSeries() => WeeklySeries(
        UserId.New(), IsoDayOfWeek.Saturday, new LocalTime(23, 0), 120, Santiago, new LocalDate(2024, 3, 30));

    private static IReadOnlyList<AvailabilityOccurrence> ExpandOver(
        AvailabilitySeries series,
        Instant windowStart,
        Instant windowEnd) =>
        AvailabilityExpansion.Expand(series, [], new TimeRange(windowStart, windowEnd), Tzdb, Santiago);

    /// <summary>A zone id the tzdb does not know, standing in for one dropped by a tzdb update.</summary>
    private sealed class RetiredZone : DateTimeZone
    {
        internal RetiredZone()
            : base("America/Atlantis", true, Offset.Zero, Offset.Zero)
        {
        }

        public override NodaTime.TimeZones.ZoneInterval GetZoneInterval(Instant instant) =>
            new("Atlantis", null, null, Offset.Zero, Offset.Zero);
    }
}
