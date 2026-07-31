using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>
/// The pure overlap engine (DOMAIN_MODEL §8). Windows are the subset-maximal intervals: for every
/// realizable non-empty subset of the selected friends, the maximal continuous intervals throughout
/// which all of them are simultaneously available. Nesting is intended — A 18–22 with B 19–20 yields
/// both 19–20 (Available 2) and 18–22 (Available 1, Maybe 1).
/// <para>
/// Construction is a boundary sweep, never subset enumeration. Every window is
/// <c>[a, b)</c> where <c>a</c> is some block's start and <c>b</c> some block's end; it is realizable
/// exactly when some block starting at <c>a</c> reaches <c>b</c> and some block ending at <c>b</c>
/// reaches back to <c>a</c>. Those two blocks belong to different friends whenever they differ, since
/// one owner's merged blocks never overlap.
/// </para>
/// </summary>
public static class OverlapEngine
{
    /// <summary>D-108: recommendations default to the next seven days.</summary>
    public const int DefaultRangeDays = 7;

    /// <summary>§12-27: D-108 forbids limiting results, not the input range.</summary>
    public const int MaximumRangeDays = 92;

    /// <summary>D-136: a Home suggestion needs at least fifteen minutes of overlap.</summary>
    public const int MinimumSuggestionMinutes = 15;

    /// <summary>§12-28: a singleton window is availability, not overlap.</summary>
    public const int MinimumSuggestionAvailable = 2;

    public const int HomeSuggestionLimit = 5;

    public static Duration MaximumRange => Duration.FromDays(MaximumRangeDays);

    public static Duration MinimumSuggestionLength => Duration.FromMinutes(MinimumSuggestionMinutes);

    public static TimeRange DefaultRange(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return TimeRange.FromDuration(clock.GetCurrentInstant(), Duration.FromDays(DefaultRangeDays));
    }

    public static void EnsureRangeAllowed(TimeRange range)
    {
        if (range.Length > MaximumRange)
        {
            throw new DomainException(
                DomainError.RangeTooLarge,
                FormattableString.Invariant(
                    $"An availability range may span at most {MaximumRangeDays} days, but was {range.Length}."));
        }
    }

    /// <summary>
    /// Every window in the range, ranked Available desc, Maybe desc, earliest start, longest.
    /// There is no result limit and duration is not an input (D-100/D-108); adjacent equivalent
    /// windows are never merged (D-136).
    /// </summary>
    public static IReadOnlyList<OverlapWindow> FindWindows(IEnumerable<FriendAvailability> friends, TimeRange range)
    {
        ArgumentNullException.ThrowIfNull(friends);
        EnsureRangeAllowed(range);

        List<(UserId Owner, List<TimeRange> Blocks)> clipped = [];
        foreach (IGrouping<UserId, FriendAvailability> group in friends.GroupBy(friend => friend.FriendId))
        {
            List<TimeRange> blocks = [];
            foreach (TimeRange merged in AvailabilityMerge.Merge(group.SelectMany(friend => friend.Blocks)))
            {
                if (merged.TryIntersect(range, out TimeRange piece))
                {
                    blocks.Add(piece);
                }
            }

            if (blocks.Count > 0)
            {
                clipped.Add((group.Key, blocks));
            }
        }

        Dictionary<Instant, Instant> maxEndByStart = [];
        Dictionary<Instant, Instant> minStartByEnd = [];
        foreach ((_, List<TimeRange> blocks) in clipped)
        {
            foreach (TimeRange block in blocks)
            {
                maxEndByStart[block.Start] = maxEndByStart.TryGetValue(block.Start, out Instant knownEnd)
                    ? Instant.Max(knownEnd, block.End)
                    : block.End;
                minStartByEnd[block.End] = minStartByEnd.TryGetValue(block.End, out Instant knownStart)
                    ? Instant.Min(knownStart, block.Start)
                    : block.Start;
            }
        }

        List<Instant> starts = [.. maxEndByStart.Keys.Order()];
        List<Instant> ends = [.. minStartByEnd.Keys.Order()];

        List<OverlapWindow> windows = [];
        foreach (Instant start in starts)
        {
            Instant reach = maxEndByStart[start];
            foreach (Instant end in ends)
            {
                if (end <= start)
                {
                    continue;
                }

                if (end > reach)
                {
                    break;
                }

                if (minStartByEnd[end] > start)
                {
                    continue;
                }

                windows.Add(Classify(clipped, new TimeRange(start, end)));
            }
        }

        return
        [
            .. windows
                .OrderByDescending(window => window.AvailableCount)
                .ThenByDescending(window => window.MaybeCount)
                .ThenBy(window => window.Range.Start)
                .ThenByDescending(window => window.Range.Length),
        ];
    }

    public static bool IsHomeSuggestion(OverlapWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Length >= MinimumSuggestionLength && window.AvailableCount >= MinimumSuggestionAvailable;
    }

    /// <summary>D-136/§12-28: the Home surface's top five, filtered but never re-ranked.</summary>
    public static IReadOnlyList<OverlapWindow> HomeSuggestions(IEnumerable<OverlapWindow> rankedWindows)
    {
        ArgumentNullException.ThrowIfNull(rankedWindows);
        return [.. rankedWindows.Where(IsHomeSuggestion).Take(HomeSuggestionLimit)];
    }

    private static OverlapWindow Classify(
        List<(UserId Owner, List<TimeRange> Blocks)> friends,
        TimeRange window)
    {
        List<UserId> available = [];
        List<UserId> maybe = [];

        foreach ((UserId owner, List<TimeRange> blocks) in friends)
        {
            if (Covers(blocks, window))
            {
                available.Add(owner);
            }
            else if (blocks.Any(block => block.Overlaps(window)))
            {
                maybe.Add(owner);
            }
        }

        return new OverlapWindow(window, available, maybe);
    }

    private static bool Covers(List<TimeRange> blocks, TimeRange window)
    {
        Instant cursor = window.Start;
        foreach (TimeRange block in blocks)
        {
            if (block.End <= cursor)
            {
                continue;
            }

            if (block.Start > cursor)
            {
                return false;
            }

            cursor = block.End;
            if (cursor >= window.End)
            {
                return true;
            }
        }

        return cursor >= window.End;
    }
}
