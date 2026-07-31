using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using static PartyUp.Domain.Tests.Availability.AvailabilityTestData;

namespace PartyUp.Domain.Tests.Availability;

public class OverlapEngineTests
{
    private static readonly UserId Ana = UserId.New();
    private static readonly UserId Bruno = UserId.New();
    private static readonly UserId Carla = UserId.New();
    private static readonly TimeRange Day = WholeDay;
    private static readonly int[] ThreeFriendAvailableCounts = [3, 2, 2, 1, 1];
    private static readonly int[] ThreeFriendMaybeCounts = [0, 1, 1, 2, 2];
    private static readonly int[] MaybeTieAvailableCounts = [3, 2, 2, 1];
    private static readonly int[] MaybeTieMaybeCounts = [0, 1, 0, 2];

    /// <summary>The DOMAIN_MODEL §8 nesting example, verbatim.</summary>
    [Fact]
    public void FindWindows_NestsFullAndPartialOverlapWindows_D035()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(18, 22)), Friend(Bruno, Hours(19, 20))],
            Day);

        Assert.Equal(2, windows.Count);

        Assert.Equal(Hours(19, 20), windows[0].Range);
        Assert.Equal(2, windows[0].AvailableCount);
        Assert.Equal(0, windows[0].MaybeCount);
        Assert.Equal(new[] { Ana, Bruno }, windows[0].Available);

        Assert.Equal(Hours(18, 22), windows[1].Range);
        Assert.Equal(new[] { Ana }, windows[1].Available);
        Assert.Equal(new[] { Bruno }, windows[1].Maybe);
    }

    [Fact]
    public void FindWindows_EnumeratesEveryRealizableSubsetForThreeFriends_D035()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(10, 14)), Friend(Bruno, Hours(11, 13)), Friend(Carla, Hours(12, 16))],
            Day);

        Assert.Equal(
            new[] { Hours(12, 13), Hours(11, 13), Hours(12, 14), Hours(10, 14), Hours(12, 16) },
            windows.Select(window => window.Range));
        Assert.Equal(ThreeFriendAvailableCounts, windows.Select(window => window.AvailableCount));
        Assert.Equal(ThreeFriendMaybeCounts, windows.Select(window => window.MaybeCount));
    }

    [Fact]
    public void FindWindows_RanksByAvailableCountBeforeMaybeCount_D052()
    {
        UserId dora = UserId.New();

        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [
                Friend(Ana, Hours(10, 12)),
                Friend(Bruno, Hours(10, 12)),
                Friend(Carla, Hours(14, 16)),
                Friend(dora, Hours(14, 16)),
                Friend(UserId.New(), Hours(14, 16)),
            ],
            Day);

        Assert.Equal(Hours(14, 16), windows[0].Range);
        Assert.Equal(3, windows[0].AvailableCount);
        Assert.Equal(Hours(10, 12), windows[1].Range);
        Assert.Equal(2, windows[1].AvailableCount);
    }

    [Fact]
    public void FindWindows_BreaksAvailableTiesByMaybeCount_D052()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [
                Friend(Ana, Hours(10, 12), Hours(14, 16)),
                Friend(Bruno, Hours(10, 12), Hours(14, 16)),
                Friend(Carla, Hours(15, 18)),
            ],
            Day);

        Assert.Equal(
            new[] { Hours(15, 16), Hours(14, 16), Hours(10, 12), Hours(15, 18) },
            windows.Select(window => window.Range));
        Assert.Equal(MaybeTieAvailableCounts, windows.Select(window => window.AvailableCount));
        Assert.Equal(MaybeTieMaybeCounts, windows.Select(window => window.MaybeCount));
    }

    [Fact]
    public void FindWindows_BreaksRemainingTiesByEarliestStart_D052()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(10, 12), Hours(14, 16)), Friend(Bruno, Hours(10, 12), Hours(14, 16))],
            Day);

        Assert.Equal(new[] { Hours(10, 12), Hours(14, 16) }, windows.Select(window => window.Range));
        Assert.All(windows, window => Assert.Equal(2, window.AvailableCount));
        Assert.All(windows, window => Assert.Equal(0, window.MaybeCount));
    }

    [Fact]
    public void FindWindows_PrefersTheWiderWindowWhenAllElseTies_D052()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(10, 14)), Friend(Bruno, Hours(10, 12))],
            Day);

        Assert.Equal(new[] { Hours(10, 12), Hours(10, 14) }, windows.Select(window => window.Range));
        Assert.Equal(2, windows[0].AvailableCount);
        Assert.Equal(1, windows[1].AvailableCount);
        Assert.Equal(1, windows[1].MaybeCount);
    }

    [Fact]
    public void FindWindows_TreatsEndpointTouchingAsNotAvailable_D101()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(10, 12)), Friend(Bruno, Hours(12, 14))],
            Day);

        Assert.Equal(2, windows.Count);
        Assert.All(windows, window => Assert.Equal(1, window.AvailableCount));
        Assert.All(windows, window => Assert.Equal(0, window.MaybeCount));
    }

    [Fact]
    public void FindWindows_NeverMergesOneOwnersAbuttingBlocksIntoOneWindow_D136()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(10, 11), Hours(11, 12))],
            Day);

        Assert.Equal(new[] { Hours(10, 11), Hours(11, 12) }, windows.Select(window => window.Range));
    }

    [Fact]
    public void FindWindows_MergesOneOwnersOverlappingBlocksBeforeMatching_D101()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(10, 12), Hours(11, 14))],
            Day);

        Assert.Equal(Hours(10, 14), Assert.Single(windows).Range);
    }

    [Fact]
    public void FindWindows_ClipsWindowsToTheRequestedRange_D100()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(8, 20)), Friend(Bruno, Hours(8, 20))],
            Hours(10, 12));

        Assert.Equal(Hours(10, 12), Assert.Single(windows).Range);
    }

    [Fact]
    public void FindWindows_ReturnsShortWindowsBecauseDurationIsNotAnInput_D100()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Minutes(10, 0, 10, 15)), Friend(Bruno, Minutes(10, 0, 10, 15))],
            Day);

        OverlapWindow only = Assert.Single(windows);
        Assert.Equal(Duration.FromMinutes(15), only.Length);
        Assert.Equal(2, only.AvailableCount);
    }

    [Fact]
    public void FindWindows_AllSelectedFriendsAreOptionalSoASingleFriendStillProducesWindows_D035()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows([Friend(Ana, Hours(18, 22))], Day);

        OverlapWindow only = Assert.Single(windows);
        Assert.Equal(1, only.AvailableCount);
        Assert.Equal(0, only.MaybeCount);
    }

    [Fact]
    public void FindWindows_WithNoAvailabilityReturnsNothing_D100()
    {
        Assert.Empty(OverlapEngine.FindWindows([], Day));
        Assert.Empty(OverlapEngine.FindWindows([Friend(Ana)], Day));
        Assert.Empty(OverlapEngine.FindWindows([Friend(Ana, Hours(2, 4))], Hours(10, 12)));
    }

    [Fact]
    public void FindWindows_AppliesNoResultLimit_D108()
    {
        List<FriendAvailability> friends =
        [
            Friend(Ana, Hours(0, 10)),
            Friend(Bruno, Hours(1, 9)),
            Friend(Carla, Hours(2, 8)),
            Friend(UserId.New(), Hours(3, 7)),
            Friend(UserId.New(), Hours(4, 6)),
        ];

        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(friends, Day);

        Assert.Equal(5, windows.Count);
        Assert.Equal(5, windows[0].AvailableCount);
        Assert.Equal(Hours(4, 6), windows[0].Range);
    }

    [Fact]
    public void DefaultRange_IsTheNextSevenDays_D108()
    {
        Instant now = At(BaseYear, 8, 3, 9, 0);
        FakeClock clock = new(now);

        TimeRange range = OverlapEngine.DefaultRange(clock);

        Assert.Equal(now, range.Start);
        Assert.Equal(Duration.FromDays(7), range.Length);
    }

    [Fact]
    public void EnsureRangeAllowed_AcceptsExactlyNinetyTwoDays_D108()
    {
        Instant now = At(BaseYear, 8, 3, 9, 0);

        OverlapEngine.EnsureRangeAllowed(TimeRange.FromDuration(now, Duration.FromDays(92)));
    }

    [Fact]
    public void FindWindows_RejectsRangesBeyondNinetyTwoDays_D108()
    {
        Instant now = At(BaseYear, 8, 3, 9, 0);
        TimeRange tooLong = TimeRange.FromDuration(now, Duration.FromDays(92) + Duration.FromMinutes(1));

        DomainException error = Assert.Throws<DomainException>(
            () => OverlapEngine.FindWindows([Friend(Ana, Hours(18, 22))], tooLong));

        Assert.Equal(DomainError.RangeTooLarge, error.Error);
    }

    [Fact]
    public void HomeSuggestions_RequireTwoAvailableFriends_D136()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(18, 22)), Friend(Bruno, Hours(19, 20))],
            Day);

        IReadOnlyList<OverlapWindow> suggestions = OverlapEngine.HomeSuggestions(windows);

        OverlapWindow only = Assert.Single(suggestions);
        Assert.Equal(Hours(19, 20), only.Range);
    }

    [Fact]
    public void HomeSuggestions_RequireFifteenMinutesOfOverlap_D136()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Minutes(10, 0, 10, 30)), Friend(Bruno, Minutes(10, 15, 10, 45))],
            Day);

        Assert.Contains(windows, window => window.Range == Minutes(10, 15, 10, 30));
        Assert.Equal(Minutes(10, 15, 10, 30), Assert.Single(OverlapEngine.HomeSuggestions(windows)).Range);
    }

    [Fact]
    public void HomeSuggestions_DropWindowsShorterThanFifteenMinutes_D136()
    {
        IReadOnlyList<OverlapWindow> windows =
        [
            new OverlapWindow(Minutes(10, 0, 10, 10), [Ana, Bruno], []),
        ];

        Assert.Empty(OverlapEngine.HomeSuggestions(windows));
    }

    [Fact]
    public void HomeSuggestions_KeepAtMostFiveAndPreserveRanking_D108()
    {
        List<FriendAvailability> friends =
        [
            Friend(Ana, Hours(0, 12)),
            Friend(Bruno, Hours(1, 11)),
            Friend(Carla, Hours(2, 10)),
            Friend(UserId.New(), Hours(3, 9)),
            Friend(UserId.New(), Hours(4, 8)),
            Friend(UserId.New(), Hours(5, 7)),
        ];
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(friends, Day);

        IReadOnlyList<OverlapWindow> suggestions = OverlapEngine.HomeSuggestions(windows);

        Assert.Equal(6, windows.Count);
        Assert.Equal(5, suggestions.Count);
        Assert.Equal(windows.Take(5).Select(window => window.Range), suggestions.Select(window => window.Range));
    }

    [Fact]
    public void IsHomeSuggestion_IsFalseForASingletonWindow_D136()
    {
        OverlapWindow singleton = new(Hours(18, 22), [Ana], [Bruno]);

        Assert.False(OverlapEngine.IsHomeSuggestion(singleton));
    }

    [Fact]
    public void FindWindows_CountsAFriendCoveringTheWindowViaAbuttingBlocksAsAvailable_D101()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(10, 11), Hours(11, 12)), Friend(Bruno, Hours(10, 12))],
            Day);

        OverlapWindow whole = Assert.Single(windows, window => window.Range == Hours(10, 12));
        Assert.Equal(2, whole.AvailableCount);
        Assert.Equal(0, whole.MaybeCount);
    }

    [Fact]
    public void FindWindows_MarksAFriendWithAGapInsideTheWindowAsMaybe_D052()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(10, 14)), Friend(Bruno, Hours(10, 11), Hours(13, 14))],
            Day);

        OverlapWindow whole = Assert.Single(windows, window => window.Range == Hours(10, 14));
        Assert.Equal(new[] { Ana }, whole.Available);
        Assert.Equal(new[] { Bruno }, whole.Maybe);
    }

    [Fact]
    public void FindWindows_DedupesIdenticalIntervalsFromDifferentSubsets_D035()
    {
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(10, 12)), Friend(Bruno, Hours(10, 12)), Friend(Carla, Hours(10, 12))],
            Day);

        OverlapWindow only = Assert.Single(windows);
        Assert.Equal(Hours(10, 12), only.Range);
        Assert.Equal(3, only.AvailableCount);
    }
}
