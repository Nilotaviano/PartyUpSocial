using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using static PartyUp.Domain.Tests.Availability.AvailabilityTestData;

namespace PartyUp.Domain.Tests.Availability;

public class AvailabilityProjectionTests
{
    private static readonly IDateTimeZoneProvider Tzdb = DateTimeZoneProviders.Tzdb;
    private static readonly LocalDate FirstMonday = new(2026, 8, 3);

    [Fact]
    public void Project_MergesOneTimeBlocksWithExpandedOccurrences_D101()
    {
        UserId owner = UserId.New();
        AvailabilitySeries series = WeeklySeries(
            owner, IsoDayOfWeek.Monday, new LocalTime(19, 0), 120, DateTimeZone.Utc, FirstMonday);
        AvailabilityBlock block = AvailabilityBlock.Create(owner, Hours(20, 23));

        IReadOnlyList<TimeRange> projected = AvailabilityProjection.Project(
            [block], [series], [], WholeDay, Tzdb, DateTimeZone.Utc);

        Assert.Equal(Hours(19, 23), Assert.Single(projected));
    }

    [Fact]
    public void Project_KeepsAbuttingBlocksAndOccurrencesDistinct_D136()
    {
        UserId owner = UserId.New();
        AvailabilitySeries series = WeeklySeries(
            owner, IsoDayOfWeek.Monday, new LocalTime(19, 0), 120, DateTimeZone.Utc, FirstMonday);
        AvailabilityBlock block = AvailabilityBlock.Create(owner, Hours(21, 23));

        IReadOnlyList<TimeRange> projected = AvailabilityProjection.Project(
            [block], [series], [], WholeDay, Tzdb, DateTimeZone.Utc);

        Assert.Equal(new[] { Hours(19, 21), Hours(21, 23) }, projected);
    }

    [Fact]
    public void Project_HonoursSkipExceptionsForTheOwningSeriesOnly_D134()
    {
        UserId owner = UserId.New();
        AvailabilitySeries skipped = WeeklySeries(
            owner, IsoDayOfWeek.Monday, new LocalTime(19, 0), 120, DateTimeZone.Utc, FirstMonday);
        AvailabilitySeries kept = WeeklySeries(
            owner, IsoDayOfWeek.Monday, new LocalTime(9, 0), 60, DateTimeZone.Utc, FirstMonday);

        IReadOnlyList<TimeRange> projected = AvailabilityProjection.Project(
            [],
            [skipped, kept],
            [AvailabilityOccurrenceSkip.For(skipped, FirstMonday)],
            WholeDay,
            Tzdb,
            DateTimeZone.Utc);

        Assert.Equal(Hours(9, 10), Assert.Single(projected));
    }

    [Fact]
    public void Project_FeedsTheOverlapEngineDirectly_D100()
    {
        UserId ana = UserId.New();
        UserId bruno = UserId.New();
        AvailabilitySeries anaSeries = WeeklySeries(
            ana, IsoDayOfWeek.Monday, new LocalTime(18, 0), 240, DateTimeZone.Utc, FirstMonday);
        AvailabilityBlock brunoBlock = AvailabilityBlock.Create(bruno, Hours(19, 20));

        IReadOnlyList<TimeRange> anaRanges = AvailabilityProjection.Project(
            [], [anaSeries], [], WholeDay, Tzdb, DateTimeZone.Utc);
        IReadOnlyList<TimeRange> brunoRanges = AvailabilityProjection.Project(
            [brunoBlock], [], [], WholeDay, Tzdb, DateTimeZone.Utc);

        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [new FriendAvailability(ana, anaRanges), new FriendAvailability(bruno, brunoRanges)],
            WholeDay);

        Assert.Equal(2, windows.Count);
        Assert.Equal(Hours(19, 20), windows[0].Range);
        Assert.Equal(2, windows[0].AvailableCount);
        Assert.Equal(Hours(18, 22), windows[1].Range);
        Assert.Equal(1, windows[1].AvailableCount);
        Assert.Equal(1, windows[1].MaybeCount);
    }

    [Fact]
    public void Project_ExcludesBlocksOutsideTheWindow_D100()
    {
        UserId owner = UserId.New();
        AvailabilityBlock inside = AvailabilityBlock.Create(owner, Hours(10, 12));
        AvailabilityBlock outside = AvailabilityBlock.Create(
            owner, new TimeRange(At(BaseYear, 8, 10, 10, 0), At(BaseYear, 8, 10, 12, 0)));

        IReadOnlyList<TimeRange> projected = AvailabilityProjection.Project(
            [inside, outside], [], [], WholeDay, Tzdb, DateTimeZone.Utc);

        Assert.Equal(Hours(10, 12), Assert.Single(projected));
    }
}
