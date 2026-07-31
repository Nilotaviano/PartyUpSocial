using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using static PartyUp.Domain.Tests.Availability.AvailabilityTestData;

namespace PartyUp.Domain.Tests.Availability;

public class AvailabilitySeriesTests
{
    private static readonly LocalDate DraggedWeekDate = new(2026, 8, 1);

    [Fact]
    public void Create_AnchorsToTheDraggedWeekAndRunsUntilDeleted_D133()
    {
        UserId owner = UserId.New();

        AvailabilitySeries series = WeeklySeries(
            owner, IsoDayOfWeek.Saturday, new LocalTime(23, 0), 120, Santiago, DraggedWeekDate);

        Assert.Equal(owner, series.OwnerId);
        Assert.Equal(IsoDayOfWeek.Saturday, series.DayOfWeek);
        Assert.Equal(new LocalTime(23, 0), series.StartTime);
        Assert.Equal(DraggedWeekDate, series.EffectiveFrom);
        Assert.Null(series.EffectiveUntil);
        Assert.Equal("America/Santiago", series.ZoneId);
        Assert.Equal(120, series.Length.TotalMinutes);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(1440)]
    public void Create_AcceptsQuarterHourLengthsUpToTwentyFourHours_D142(int minutes)
    {
        AvailabilitySeries series = WeeklySeries(
            UserId.New(), IsoDayOfWeek.Saturday, new LocalTime(23, 0), minutes, Santiago, DraggedWeekDate);

        Assert.Equal(minutes, series.Length.TotalMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(1455)]
    public void Create_RejectsLengthsOffTheQuarterHourGridOrOutOfBounds_D142(int minutes)
    {
        DomainException error = Assert.Throws<DomainException>(() => Duration15.FromMinutes(minutes));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Theory]
    [InlineData(23, 7)]
    [InlineData(0, 1)]
    [InlineData(12, 20)]
    public void Create_RejectsStartTimesOffTheQuarterHourGrid_D071(int hour, int minute)
    {
        DomainException error = Assert.Throws<DomainException>(() => WeeklyPattern.Create(
            IsoDayOfWeek.Saturday, new LocalTime(hour, minute), Duration15.FromMinutes(120)));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_RejectsAnEffectiveUntilAtOrBeforeEffectiveFrom_D133()
    {
        WeeklyPattern pattern = WeeklyPattern.Create(
            IsoDayOfWeek.Saturday, new LocalTime(23, 0), Duration15.FromMinutes(120));

        Assert.Throws<DomainException>(() => AvailabilitySeries.Create(
            UserId.New(), pattern, Santiago, DraggedWeekDate, DraggedWeekDate));
        Assert.Throws<DomainException>(() => AvailabilitySeries.Create(
            UserId.New(), pattern, Santiago, DraggedWeekDate, DraggedWeekDate.PlusDays(-1)));
    }

    [Fact]
    public void IsOccurrenceDate_RequiresTheSeriesWeekdayInsideTheEffectiveRange_D025()
    {
        AvailabilitySeries series = WeeklySeries(
            UserId.New(), IsoDayOfWeek.Saturday, new LocalTime(23, 0), 120, Santiago, DraggedWeekDate);
        series.TruncateAt(new LocalDate(2026, 8, 24));

        Assert.True(series.IsOccurrenceDate(new LocalDate(2026, 8, 1)));
        Assert.True(series.IsOccurrenceDate(new LocalDate(2026, 8, 22)));
        Assert.False(series.IsOccurrenceDate(new LocalDate(2026, 8, 29)));
        Assert.False(series.IsOccurrenceDate(new LocalDate(2026, 7, 25)));
        Assert.False(series.IsOccurrenceDate(new LocalDate(2026, 8, 9)));
    }

    [Fact]
    public void ChangeZone_MovesTheSeriesToTheSameLocalClockTime_D061()
    {
        AvailabilitySeries series = WeeklySeries(
            UserId.New(), IsoDayOfWeek.Saturday, new LocalTime(23, 0), 120, Santiago, DraggedWeekDate);

        series.ChangeZone(DateTimeZoneProviders.Tzdb["Europe/Lisbon"]);

        Assert.Equal("Europe/Lisbon", series.ZoneId);
        Assert.Equal(new LocalTime(23, 0), series.StartTime);
        Assert.Equal(IsoDayOfWeek.Saturday, series.DayOfWeek);
        Assert.Equal(120, series.Length.TotalMinutes);
        Assert.Equal(DraggedWeekDate, series.EffectiveFrom);
    }

    [Fact]
    public void TruncateAt_RejectsExtendingAnAlreadyClippedSeries_D133()
    {
        AvailabilitySeries series = WeeklySeries(
            UserId.New(), IsoDayOfWeek.Saturday, new LocalTime(23, 0), 120, Santiago, DraggedWeekDate);
        series.TruncateAt(new LocalDate(2026, 8, 17));

        Assert.Throws<DomainException>(() => series.TruncateAt(new LocalDate(2026, 8, 31)));
        Assert.Throws<DomainException>(() => series.TruncateAt(DraggedWeekDate.PlusDays(-1)));
    }

    [Fact]
    public void TruncateAt_EffectiveFrom_LeavesAnEmptySeries_D143()
    {
        AvailabilitySeries series = WeeklySeries(
            UserId.New(), IsoDayOfWeek.Saturday, new LocalTime(23, 0), 120, Santiago, DraggedWeekDate);

        series.TruncateAt(DraggedWeekDate);

        Assert.True(series.IsEmpty);
        Assert.False(series.IsOccurrenceDate(DraggedWeekDate));
    }

    [Fact]
    public void Continue_KeepsOwnerAndZoneButTakesTheNewPattern_D143()
    {
        AvailabilitySeries series = WeeklySeries(
            UserId.New(), IsoDayOfWeek.Saturday, new LocalTime(23, 0), 120, Santiago, DraggedWeekDate);

        AvailabilitySeries successor = series.Continue(
            WeeklyPattern.Create(IsoDayOfWeek.Sunday, new LocalTime(20, 30), Duration15.FromMinutes(90)),
            new LocalDate(2026, 8, 17),
            null);

        Assert.NotEqual(series.Id, successor.Id);
        Assert.Equal(series.OwnerId, successor.OwnerId);
        Assert.Equal(series.ZoneId, successor.ZoneId);
        Assert.Equal(IsoDayOfWeek.Sunday, successor.DayOfWeek);
        Assert.Equal(new LocalTime(20, 30), successor.StartTime);
        Assert.Equal(90, successor.Length.TotalMinutes);
    }

    [Fact]
    public void Pattern_RoundTripsThroughTheStoredScalars_D132()
    {
        AvailabilitySeries series = WeeklySeries(
            UserId.New(), IsoDayOfWeek.Wednesday, new LocalTime(19, 45), 45, Santiago, DraggedWeekDate);

        WeeklyPattern pattern = series.Pattern;

        Assert.Equal(IsoDayOfWeek.Wednesday, pattern.DayOfWeek);
        Assert.Equal(new LocalTime(19, 45), pattern.StartTime);
        Assert.Equal(45, pattern.Length.TotalMinutes);
        Assert.Equal(Period.FromMinutes(45), pattern.ToPeriod());
    }
}
