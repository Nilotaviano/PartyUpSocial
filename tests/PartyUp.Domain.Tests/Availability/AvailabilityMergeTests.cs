using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using static PartyUp.Domain.Tests.Availability.AvailabilityTestData;

namespace PartyUp.Domain.Tests.Availability;

public class AvailabilityMergeTests
{
    [Fact]
    public void Merge_CoalescesStrictlyOverlappingIntervals_D101()
    {
        IReadOnlyList<TimeRange> merged = AvailabilityMerge.Merge([Hours(18, 21), Hours(20, 23)]);

        TimeRange only = Assert.Single(merged);
        Assert.Equal(Hours(18, 23), only);
    }

    [Fact]
    public void Merge_KeepsAbuttingIntervalsDistinct_D136()
    {
        IReadOnlyList<TimeRange> merged = AvailabilityMerge.Merge([Hours(18, 20), Hours(20, 22)]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(Hours(18, 20), merged[0]);
        Assert.Equal(Hours(20, 22), merged[1]);
    }

    [Fact]
    public void Merge_SwallowsAnEnclosedInterval_D101()
    {
        IReadOnlyList<TimeRange> merged = AvailabilityMerge.Merge([Hours(18, 23), Hours(19, 20)]);

        Assert.Equal(Hours(18, 23), Assert.Single(merged));
    }

    [Fact]
    public void Merge_AcceptsUnsortedInputAndReturnsAscendingOrder_D101()
    {
        IReadOnlyList<TimeRange> merged = AvailabilityMerge.Merge(
            [Hours(22, 23), Hours(9, 10), Hours(18, 21), Hours(20, 22)]);

        Assert.Equal(new[] { Hours(9, 10), Hours(18, 22), Hours(22, 23) }, merged);
    }

    [Fact]
    public void Merge_ChainsAThreeWayOverlapIntoOneInterval_D101()
    {
        IReadOnlyList<TimeRange> merged = AvailabilityMerge.Merge(
            [Hours(10, 12), Hours(11, 14), Hours(13, 16)]);

        Assert.Equal(Hours(10, 16), Assert.Single(merged));
    }

    [Fact]
    public void Merge_LeavesDisjointIntervalsAlone_D101()
    {
        IReadOnlyList<TimeRange> merged = AvailabilityMerge.Merge([Hours(10, 12), Hours(14, 16)]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_OfNothingIsEmpty_D101()
    {
        Assert.Empty(AvailabilityMerge.Merge([]));
    }

    [Fact]
    public void Merge_CollapsesDuplicateIntervals_D101()
    {
        IReadOnlyList<TimeRange> merged = AvailabilityMerge.Merge([Hours(18, 22), Hours(18, 22)]);

        Assert.Equal(Hours(18, 22), Assert.Single(merged));
    }
}
