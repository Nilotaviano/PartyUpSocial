using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Tests.Kernel;

public class TimeRangeTests
{
    private static readonly Instant Eight = Instant.FromUtc(2026, 7, 30, 20, 0);
    private static readonly Instant Nine = Instant.FromUtc(2026, 7, 30, 21, 0);
    private static readonly Instant Ten = Instant.FromUtc(2026, 7, 30, 22, 0);
    private static readonly Instant Eleven = Instant.FromUtc(2026, 7, 30, 23, 0);

    [Fact]
    public void Constructor_RejectsAZeroLengthRange()
    {
        DomainException error = Assert.Throws<DomainException>(() => new TimeRange(Eight, Eight));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Constructor_RejectsAnInvertedRange()
    {
        Assert.Throws<DomainException>(() => new TimeRange(Ten, Eight));
    }

    [Fact]
    public void FromDuration_DerivesTheEndFromTheLength()
    {
        TimeRange range = TimeRange.FromDuration(Eight, Duration.FromHours(2));

        Assert.Equal(Eight, range.Start);
        Assert.Equal(Ten, range.End);
        Assert.Equal(Duration.FromHours(2), range.Length);
    }

    [Fact]
    public void Contains_IncludesTheStartInstant()
    {
        Assert.True(new TimeRange(Eight, Ten).Contains(Eight));
    }

    [Fact]
    public void Contains_ExcludesTheEndInstant()
    {
        Assert.False(new TimeRange(Eight, Ten).Contains(Ten));
    }

    [Fact]
    public void Contains_ExcludesInstantsOutsideTheRange()
    {
        TimeRange range = new(Nine, Ten);

        Assert.False(range.Contains(Eight));
        Assert.True(range.Contains(Nine));
        Assert.False(range.Contains(Eleven));
    }

    [Fact]
    public void Overlaps_IsFalseForAbuttingRanges()
    {
        TimeRange first = new(Eight, Ten);
        TimeRange second = new(Ten, Eleven);

        Assert.False(first.Overlaps(second));
        Assert.False(second.Overlaps(first));
    }

    [Fact]
    public void Overlaps_IsTrueForPartialOverlapInBothDirections()
    {
        TimeRange first = new(Eight, Ten);
        TimeRange second = new(Nine, Eleven);

        Assert.True(first.Overlaps(second));
        Assert.True(second.Overlaps(first));
    }

    [Fact]
    public void Overlaps_IsTrueForNestedAndIdenticalRanges()
    {
        TimeRange outer = new(Eight, Eleven);
        TimeRange inner = new(Nine, Ten);

        Assert.True(outer.Overlaps(inner));
        Assert.True(inner.Overlaps(outer));
        Assert.True(outer.Overlaps(outer));
    }

    [Fact]
    public void Overlaps_IsFalseForDisjointRanges()
    {
        TimeRange first = new(Eight, Nine);
        TimeRange second = new(Ten, Eleven);

        Assert.False(first.Overlaps(second));
        Assert.False(second.Overlaps(first));
    }

    [Fact]
    public void Abuts_IsTrueOnlyWhenEndpointsTouch()
    {
        TimeRange first = new(Eight, Ten);

        Assert.True(first.Abuts(new TimeRange(Ten, Eleven)));
        Assert.True(new TimeRange(Ten, Eleven).Abuts(first));
        Assert.False(first.Abuts(new TimeRange(Eleven, Eleven + Duration.FromHours(1))));
        Assert.False(first.Abuts(new TimeRange(Nine, Eleven)));
    }

    [Fact]
    public void Encloses_IsTrueForContainedAndIdenticalRanges()
    {
        TimeRange outer = new(Eight, Eleven);

        Assert.True(outer.Encloses(new TimeRange(Nine, Ten)));
        Assert.True(outer.Encloses(outer));
        Assert.True(outer.Encloses(new TimeRange(Eight, Nine)));
        Assert.False(outer.Encloses(new TimeRange(Nine, Eleven + Duration.FromMinutes(1))));
        Assert.False(new TimeRange(Nine, Ten).Encloses(outer));
    }

    [Fact]
    public void TryIntersect_ReturnsTheCommonRange()
    {
        bool intersects = new TimeRange(Eight, Ten).TryIntersect(new TimeRange(Nine, Eleven), out TimeRange common);

        Assert.True(intersects);
        Assert.Equal(new TimeRange(Nine, Ten), common);
    }

    [Fact]
    public void TryIntersect_ReturnsTheInnerRangeWhenNested()
    {
        bool intersects = new TimeRange(Eight, Eleven).TryIntersect(new TimeRange(Nine, Ten), out TimeRange common);

        Assert.True(intersects);
        Assert.Equal(new TimeRange(Nine, Ten), common);
    }

    [Fact]
    public void TryIntersect_FailsForAbuttingRanges()
    {
        bool intersects = new TimeRange(Eight, Ten).TryIntersect(new TimeRange(Ten, Eleven), out TimeRange common);

        Assert.False(intersects);
        Assert.Equal(default, common);
    }

    [Fact]
    public void TryIntersect_FailsForDisjointRanges()
    {
        Assert.False(new TimeRange(Eight, Nine).TryIntersect(new TimeRange(Ten, Eleven), out _));
    }

    [Fact]
    public void Equality_IsByEndpoints()
    {
        Assert.Equal(new TimeRange(Eight, Ten), new TimeRange(Eight, Ten));
        Assert.NotEqual(new TimeRange(Eight, Ten), new TimeRange(Eight, Eleven));
    }

    [Fact]
    public void ARangeEndingNowDoesNotContainNow()
    {
        FakeClock clock = new(Eight);
        TimeRange range = TimeRange.FromDuration(clock.GetCurrentInstant(), Duration.FromHours(2));

        Assert.True(range.Contains(clock.GetCurrentInstant()));

        clock.Advance(Duration.FromHours(2));

        Assert.False(range.Contains(clock.GetCurrentInstant()));
    }

    [Fact]
    public void ToString_RendersHalfOpenNotation()
    {
        Assert.StartsWith("[", new TimeRange(Eight, Ten).ToString(), StringComparison.Ordinal);
        Assert.EndsWith(")", new TimeRange(Eight, Ten).ToString(), StringComparison.Ordinal);
    }
}
