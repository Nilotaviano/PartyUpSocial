using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using static PartyUp.Domain.Tests.Availability.AvailabilityTestData;

namespace PartyUp.Domain.Tests.Availability;

/// <summary>
/// D-023's recommendation promise and D-116's Home budget, which the ranking and filter tests only
/// cover incidentally.
/// </summary>
public class OverlapRecommendationTests
{
    private static readonly UserId Ana = UserId.New();
    private static readonly UserId Bruno = UserId.New();
    private static readonly UserId Carla = UserId.New();

    [Fact]
    public void FindWindows_RecommendsTheTimesFriendsShare_D023()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(18, 21)), Friend(Bruno, Hours(19, 22)), Friend(Carla, Hours(9, 10))],
            WholeDay);

        OverlapWindow best = windows[0];
        Assert.Equal(Hours(19, 21), best.Range);
        Assert.Equal(2, best.AvailableCount);
        Assert.DoesNotContain(Carla, best.Available);
        Assert.Contains(windows, window => window.Range == Hours(9, 10));
    }

    [Fact]
    public void FindWindows_RecommendsNothingWhenNobodyIsAvailable_D023()
    {
        Assert.Empty(OverlapEngine.FindWindows([], WholeDay));
    }

    [Fact]
    public void HomeSuggestions_StopAtFiveAndLeaveTheRestForSeeAll_D116()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [
                Friend(Ana, Hours(0, 12)),
                Friend(Bruno, Hours(1, 11)),
                Friend(Carla, Hours(2, 10)),
                Friend(UserId.New(), Hours(3, 9)),
                Friend(UserId.New(), Hours(4, 8)),
                Friend(UserId.New(), Hours(5, 7)),
            ],
            WholeDay);

        IReadOnlyList<OverlapWindow> suggestions = OverlapEngine.HomeSuggestions(windows);

        Assert.Equal(OverlapEngine.HomeSuggestionLimit, suggestions.Count);
        Assert.True(windows.Count > suggestions.Count);
        Assert.All(suggestions, window => Assert.True(OverlapEngine.IsHomeSuggestion(window)));
    }
}
