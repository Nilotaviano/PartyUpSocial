using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using static PartyUp.Domain.Tests.Availability.AvailabilityTestData;

namespace PartyUp.Domain.Tests.Availability;

public class AvailabilitySeriesEditorTests
{
    // 2026-08-01 is a Saturday, and the series starts on it.
    private static readonly LocalDate FirstSaturday = new(2026, 8, 1);
    private static readonly LocalDate SecondSaturday = new(2026, 8, 8);
    private static readonly LocalDate ThirdSaturday = new(2026, 8, 15);

    [Fact]
    public void DeleteOccurrence_SuppressesOnlyThatOccurrence_D025()
    {
        AvailabilitySeries series = Series();

        AvailabilityOccurrenceSkip skip = AvailabilitySeriesEditor.DeleteOccurrence(series, SecondSaturday);

        Assert.Equal(series.Id, skip.SeriesId);
        Assert.Equal(SecondSaturday, skip.OccurrenceDate);
        Assert.Null(series.EffectiveUntil);
        Assert.True(series.IsOccurrenceDate(ThirdSaturday));
    }

    [Fact]
    public void DeleteOccurrence_RejectsADateOutsideTheSeries_D134()
    {
        AvailabilitySeries series = Series();

        DomainException error = Assert.Throws<DomainException>(
            () => AvailabilitySeriesEditor.DeleteOccurrence(series, SecondSaturday.PlusDays(1)));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void EditOccurrence_ProducesASkipPlusAMaterializedBlock_D134()
    {
        AvailabilitySeries series = Series();
        TimeRange replacement = new(At(2026, 8, 9, 1, 0), At(2026, 8, 9, 4, 0));

        OccurrenceEditResult result = AvailabilitySeriesEditor.EditOccurrence(
            series, SecondSaturday, replacement);

        Assert.Equal(SecondSaturday, result.Skip.OccurrenceDate);
        Assert.Equal(series.Id, result.Skip.SeriesId);
        Assert.Equal(series.Id, result.Block.SourceSeriesId);
        Assert.Equal(SecondSaturday, result.Block.SourceOccurrenceDate);
        Assert.Equal(replacement, result.Block.Range);
        Assert.Equal(series.OwnerId, result.Block.OwnerId);
        Assert.Null(series.EffectiveUntil);
    }

    [Fact]
    public void EditOccurrence_MaterializedBlockDoesNotFollowALaterZoneChange_D061()
    {
        AvailabilitySeries series = Series();
        TimeRange replacement = new(At(2026, 8, 9, 1, 0), At(2026, 8, 9, 4, 0));
        OccurrenceEditResult result = AvailabilitySeriesEditor.EditOccurrence(
            series, SecondSaturday, replacement);

        series.ChangeZone(DateTimeZoneProviders.Tzdb["Pacific/Auckland"]);

        Assert.Equal("Pacific/Auckland", series.ZoneId);
        Assert.Equal(replacement, result.Block.Range);
    }

    [Fact]
    public void SplitFromOccurrence_SplitsAtTheSelectedOccurrenceDate_D143()
    {
        AvailabilitySeries series = Series();
        WeeklyPattern replacement = WeeklyPattern.Create(
            IsoDayOfWeek.Sunday, new LocalTime(20, 0), Duration15.FromMinutes(90));

        SeriesSplitResult result = AvailabilitySeriesEditor.SplitFromOccurrence(
            series, ThirdSaturday, replacement, [], []);

        Assert.Equal(ThirdSaturday, result.SplitDate);
        Assert.Equal(ThirdSaturday, series.EffectiveUntil);
        Assert.Equal(ThirdSaturday, result.Successor.EffectiveFrom);
        Assert.Null(result.Successor.EffectiveUntil);
        Assert.Equal(IsoDayOfWeek.Sunday, result.Successor.DayOfWeek);
        Assert.Equal(new LocalTime(20, 0), result.Successor.StartTime);
        Assert.Equal(series.OwnerId, result.Successor.OwnerId);
        Assert.Equal(series.ZoneId, result.Successor.ZoneId);
        Assert.False(result.OriginalBecameEmpty);
    }

    [Fact]
    public void SplitFromOccurrence_LeavesEarlierOccurrencesOnTheOriginalPattern_D143()
    {
        AvailabilitySeries series = Series();
        WeeklyPattern replacement = WeeklyPattern.Create(
            IsoDayOfWeek.Sunday, new LocalTime(20, 0), Duration15.FromMinutes(90));

        AvailabilitySeriesEditor.SplitFromOccurrence(series, ThirdSaturday, replacement, [], []);

        Assert.True(series.IsOccurrenceDate(FirstSaturday));
        Assert.True(series.IsOccurrenceDate(SecondSaturday));
        Assert.False(series.IsOccurrenceDate(ThirdSaturday));
    }

    /// <summary>
    /// §12-20: a weekday change takes effect from the first new-weekday date on or after the split —
    /// the earlier Tuesday of the same week belongs to the untouched past.
    /// </summary>
    [Fact]
    public void SplitFromOccurrence_MovesTheWeekdayForwardFromTheSplitOnly_D143()
    {
        AvailabilitySeries series = Series();
        WeeklyPattern replacement = WeeklyPattern.Create(
            IsoDayOfWeek.Tuesday, new LocalTime(20, 0), Duration15.FromMinutes(90));

        SeriesSplitResult result = AvailabilitySeriesEditor.SplitFromOccurrence(
            series, ThirdSaturday, replacement, [], []);

        Assert.False(result.Successor.IsOccurrenceDate(new LocalDate(2026, 8, 11)));
        Assert.True(result.Successor.IsOccurrenceDate(new LocalDate(2026, 8, 18)));
    }

    [Fact]
    public void SplitFromOccurrence_DropsFutureSkipsAndMaterializedBlocks_D143()
    {
        AvailabilitySeries series = Series();
        AvailabilityOccurrenceSkip pastSkip = AvailabilityOccurrenceSkip.For(series, FirstSaturday);
        AvailabilityOccurrenceSkip splitSkip = AvailabilityOccurrenceSkip.For(series, ThirdSaturday);
        AvailabilityOccurrenceSkip futureSkip = AvailabilityOccurrenceSkip.For(series, new LocalDate(2026, 8, 22));
        AvailabilityBlock pastBlock = AvailabilityBlock.Materialize(
            series, SecondSaturday, new TimeRange(At(2026, 8, 9, 1, 0), At(2026, 8, 9, 3, 0)));
        AvailabilityBlock futureBlock = AvailabilityBlock.Materialize(
            series, new LocalDate(2026, 8, 29), new TimeRange(At(2026, 8, 30, 1, 0), At(2026, 8, 30, 3, 0)));
        AvailabilityBlock unrelated = AvailabilityBlock.Create(series.OwnerId, Hours(18, 20));

        SeriesSplitResult result = AvailabilitySeriesEditor.SplitFromOccurrence(
            series,
            ThirdSaturday,
            series.Pattern,
            [pastSkip, splitSkip, futureSkip],
            [pastBlock, futureBlock, unrelated]);

        Assert.Equal([splitSkip, futureSkip], result.DroppedSkips);
        Assert.Equal(futureBlock, Assert.Single(result.DroppedBlocks));
    }

    [Fact]
    public void SplitFromOccurrence_AtTheFirstOccurrenceEmptiesTheOriginal_D143()
    {
        AvailabilitySeries series = Series();
        WeeklyPattern replacement = WeeklyPattern.Create(
            IsoDayOfWeek.Sunday, new LocalTime(20, 0), Duration15.FromMinutes(90));

        SeriesSplitResult result = AvailabilitySeriesEditor.SplitFromOccurrence(
            series, FirstSaturday, replacement, [], []);

        Assert.Equal(FirstSaturday, result.SplitDate);
        Assert.True(result.OriginalBecameEmpty);
        Assert.True(series.IsEmpty);
        Assert.Equal(FirstSaturday, result.Successor.EffectiveFrom);
    }

    [Fact]
    public void SplitFromOccurrence_CarriesTheOriginalEffectiveUntilToTheSuccessor_D143()
    {
        AvailabilitySeries series = Series();
        series.TruncateAt(new LocalDate(2026, 9, 1));
        WeeklyPattern replacement = WeeklyPattern.Create(
            IsoDayOfWeek.Sunday, new LocalTime(20, 0), Duration15.FromMinutes(90));

        SeriesSplitResult result = AvailabilitySeriesEditor.SplitFromOccurrence(
            series, ThirdSaturday, replacement, [], []);

        Assert.Equal(new LocalDate(2026, 9, 1), result.Successor.EffectiveUntil);
        Assert.Equal(ThirdSaturday, series.EffectiveUntil);
    }

    [Fact]
    public void SplitFromOccurrence_RejectsADateThatIsNotAnOccurrence_D143()
    {
        AvailabilitySeries series = Series();

        Assert.Throws<DomainException>(() => AvailabilitySeriesEditor.SplitFromOccurrence(
            series, ThirdSaturday.PlusDays(2), series.Pattern, [], []));
    }

    [Fact]
    public void DeleteSeries_CascadesToSkipsAndMaterializedBlocks_D025()
    {
        AvailabilitySeries series = Series();
        AvailabilitySeries other = Series();
        AvailabilityOccurrenceSkip mine = AvailabilityOccurrenceSkip.For(series, FirstSaturday);
        AvailabilityOccurrenceSkip theirs = AvailabilityOccurrenceSkip.For(other, FirstSaturday);
        AvailabilityBlock myBlock = AvailabilityBlock.Materialize(
            series, SecondSaturday, new TimeRange(At(2026, 8, 9, 1, 0), At(2026, 8, 9, 3, 0)));
        AvailabilityBlock standalone = AvailabilityBlock.Create(series.OwnerId, Hours(18, 20));

        SeriesDeletionResult result = AvailabilitySeriesEditor.DeleteSeries(
            series, [mine, theirs], [myBlock, standalone]);

        Assert.Equal(mine, Assert.Single(result.CascadedSkips));
        Assert.Equal(myBlock, Assert.Single(result.CascadedBlocks));
    }

    private static AvailabilitySeries Series() => WeeklySeries(
        UserId.New(), IsoDayOfWeek.Saturday, new LocalTime(23, 0), 120, Santiago, FirstSaturday);
}
