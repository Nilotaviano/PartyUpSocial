using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Tests.Kernel;

public class Duration15Tests
{
    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(720)]
    [InlineData(1425)]
    [InlineData(1440)]
    public void FromMinutes_AcceptsQuarterHourValuesInRange_D142(int minutes)
    {
        Duration15 duration = Duration15.FromMinutes(minutes);

        Assert.Equal(minutes, duration.TotalMinutes);
        Assert.Equal(Duration.FromMinutes(minutes), duration.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(-15)]
    [InlineData(-1440)]
    [InlineData(1441)]
    [InlineData(1455)]
    [InlineData(2880)]
    public void FromMinutes_RejectsValuesOutsideFifteenMinutesToTwentyFourHours_D142(int minutes)
    {
        DomainException error = Assert.Throws<DomainException>(() => Duration15.FromMinutes(minutes));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(29)]
    [InlineData(59)]
    [InlineData(100)]
    [InlineData(1439)]
    public void FromMinutes_RejectsValuesOffTheFifteenMinuteGrid_D142(int minutes)
    {
        DomainException error = Assert.Throws<DomainException>(() => Duration15.FromMinutes(minutes));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Bounds_AreFifteenMinutesAndTwentyFourHours_D142()
    {
        Assert.Equal(15, Duration15.MinimumMinutes);
        Assert.Equal(1440, Duration15.MaximumMinutes);
        Assert.Equal(15, Duration15.GranularityMinutes);
        Assert.Equal(Duration.FromMinutes(15), Duration15.MinimumValue);
        Assert.Equal(Duration.FromHours(24), Duration15.MaximumValue);
        Assert.Equal(Duration.FromMinutes(15), Duration15.Granularity);
    }

    [Fact]
    public void FromDuration_AcceptsAnExactQuarterHourDuration_D142()
    {
        Duration15 duration = Duration15.FromDuration(Duration.FromMinutes(90));

        Assert.Equal(90, duration.TotalMinutes);
    }

    [Fact]
    public void FromDuration_AcceptsBothBounds_D142()
    {
        Assert.Equal(15, Duration15.FromDuration(Duration.FromMinutes(15)).TotalMinutes);
        Assert.Equal(1440, Duration15.FromDuration(Duration.FromHours(24)).TotalMinutes);
    }

    [Fact]
    public void FromDuration_RejectsOneTickBelowTheMinimum_D142()
    {
        Duration justUnder = Duration.FromMinutes(15) - Duration.FromTicks(1);

        DomainException error = Assert.Throws<DomainException>(() => Duration15.FromDuration(justUnder));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void FromDuration_RejectsOneTickAboveTheMaximum_D142()
    {
        Duration justOver = Duration.FromHours(24) + Duration.FromTicks(1);

        DomainException error = Assert.Throws<DomainException>(() => Duration15.FromDuration(justOver));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void FromDuration_RejectsSubMinutePrecision_D142()
    {
        Duration withSeconds = Duration.FromMinutes(30) + Duration.FromSeconds(1);

        Assert.Throws<DomainException>(() => Duration15.FromDuration(withSeconds));
    }

    [Fact]
    public void FromDuration_RejectsSubTickPrecision_D142()
    {
        Duration withNanoseconds = Duration.FromMinutes(30) + Duration.FromNanoseconds(1);

        Assert.Throws<DomainException>(() => Duration15.FromDuration(withNanoseconds));
    }

    [Fact]
    public void FromDuration_RejectsNegativeAndZeroDurations_D142()
    {
        Assert.Throws<DomainException>(() => Duration15.FromDuration(Duration.Zero));
        Assert.Throws<DomainException>(() => Duration15.FromDuration(Duration.FromMinutes(-30)));
    }

    [Fact]
    public void FromDuration_RoundTripsThroughValue_D142()
    {
        Duration15 original = Duration15.FromMinutes(345);

        Assert.Equal(original, Duration15.FromDuration(original.Value));
    }

    [Fact]
    public void Equality_IsByLength()
    {
        Assert.Equal(Duration15.FromMinutes(60), Duration15.FromDuration(Duration.FromHours(1)));
        Assert.NotEqual(Duration15.FromMinutes(60), Duration15.FromMinutes(75));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(15, true)]
    [InlineData(45, true)]
    [InlineData(1440, true)]
    [InlineData(1, false)]
    [InlineData(14, false)]
    [InlineData(-15, true)]
    [InlineData(-7, false)]
    public void IsOnGrid_TracksFifteenMinuteBoundaries_D071(int minutesFromEpoch, bool expected)
    {
        Instant instant = NodaConstants.UnixEpoch + Duration.FromMinutes(minutesFromEpoch);

        Assert.Equal(expected, Duration15.IsOnGrid(instant));
        Assert.Equal(expected, Duration15.IsOnGrid(Duration.FromMinutes(minutesFromEpoch)));
    }

    [Fact]
    public void IsOnGrid_RejectsSubTickPrecision_D071()
    {
        Assert.False(Duration15.IsOnGrid(Duration.FromMinutes(15) + Duration.FromNanoseconds(1)));
    }

    [Fact]
    public void ToString_RendersMinutes()
    {
        Assert.Equal("90m", Duration15.FromMinutes(90).ToString());
    }
}
