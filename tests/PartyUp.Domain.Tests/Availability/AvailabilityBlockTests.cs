using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using static PartyUp.Domain.Tests.Availability.AvailabilityTestData;

namespace PartyUp.Domain.Tests.Availability;

public class AvailabilityBlockTests
{
    [Fact]
    public void Create_KeepsHalfOpenBoundsAndOwner_D132()
    {
        UserId owner = UserId.New();

        AvailabilityBlock block = AvailabilityBlock.Create(owner, Hours(18, 22));

        Assert.Equal(owner, block.OwnerId);
        Assert.Equal(At(BaseYear, 8, 3, 18, 0), block.Start);
        Assert.Equal(At(BaseYear, 8, 3, 22, 0), block.End);
        Assert.Equal(Duration.FromHours(4), block.Range.Length);
        Assert.False(block.IsMaterializedFromSeries);
        Assert.Null(block.SourceSeriesId);
        Assert.Null(block.SourceOccurrenceDate);
    }

    [Fact]
    public void Create_AllowsExactlyFifteenMinutes_D071()
    {
        AvailabilityBlock block = AvailabilityBlock.Create(UserId.New(), Minutes(18, 0, 18, 15));

        Assert.Equal(Duration.FromMinutes(15), block.Range.Length);
    }

    [Theory]
    [InlineData(18, 7, 19, 0)]
    [InlineData(18, 0, 19, 1)]
    [InlineData(18, 5, 19, 10)]
    [InlineData(18, 14, 18, 29)]
    public void Create_RejectsOffGridEndpoints_D071(int startHour, int startMinute, int endHour, int endMinute)
    {
        TimeRange offGrid = new(
            At(BaseYear, 8, 3, startHour, startMinute),
            At(BaseYear, 8, 3, endHour, endMinute));

        DomainException error = Assert.Throws<DomainException>(
            () => AvailabilityBlock.Create(UserId.New(), offGrid));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_RejectsZeroLengthRange_D071()
    {
        Instant instant = At(BaseYear, 8, 3, 18, 0);

        Assert.Throws<DomainException>(() => new TimeRange(instant, instant));
    }

    [Fact]
    public void Create_AllowsBlocksSpanningMidnight_D132()
    {
        AvailabilityBlock block = AvailabilityBlock.Create(
            UserId.New(),
            new TimeRange(At(BaseYear, 8, 3, 23, 0), At(BaseYear, 8, 4, 1, 0)));

        Assert.Equal(Duration.FromHours(2), block.Range.Length);
    }

    [Fact]
    public void Materialize_CarriesSourceSeriesAndOccurrenceDate_D143()
    {
        UserId owner = UserId.New();
        AvailabilitySeries series = WeeklySeries(
            owner, IsoDayOfWeek.Saturday, new LocalTime(23, 0), 120, Santiago, new LocalDate(2026, 8, 1));

        AvailabilityBlock block = AvailabilityBlock.Materialize(
            series,
            new LocalDate(2026, 8, 8),
            new TimeRange(At(2026, 8, 9, 1, 0), At(2026, 8, 9, 3, 0)));

        Assert.True(block.IsMaterializedFromSeries);
        Assert.Equal(series.Id, block.SourceSeriesId);
        Assert.Equal(new LocalDate(2026, 8, 8), block.SourceOccurrenceDate);
        Assert.Equal(owner, block.OwnerId);
    }

    [Fact]
    public void Materialize_RejectsADateThatIsNotAnOccurrence_D143()
    {
        AvailabilitySeries series = WeeklySeries(
            UserId.New(), IsoDayOfWeek.Saturday, new LocalTime(23, 0), 120, Santiago, new LocalDate(2026, 8, 1));

        DomainException error = Assert.Throws<DomainException>(() => AvailabilityBlock.Materialize(
            series,
            new LocalDate(2026, 8, 9),
            new TimeRange(At(2026, 8, 9, 1, 0), At(2026, 8, 9, 3, 0))));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void Reschedule_MovesTheBlockAndKeepsTheGridRule_D071()
    {
        AvailabilityBlock block = AvailabilityBlock.Create(UserId.New(), Hours(18, 22));

        block.Reschedule(Minutes(19, 15, 20, 45));

        Assert.Equal(At(BaseYear, 8, 3, 19, 15), block.Start);
        Assert.Equal(At(BaseYear, 8, 3, 20, 45), block.End);
        Assert.Throws<DomainException>(() => block.Reschedule(
            new TimeRange(At(BaseYear, 8, 3, 19, 20), At(BaseYear, 8, 3, 20, 45))));
    }
}
