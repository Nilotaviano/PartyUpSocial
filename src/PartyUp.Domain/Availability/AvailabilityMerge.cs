using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>
/// D-101: one owner's *strictly overlapping* intervals coalesce, for display and as overlap-engine
/// input. Abutting intervals stay distinct (half-open, §1) — that is what keeps D-136's
/// "do not merge adjacent equivalent windows" rule meaningful.
/// </summary>
public static class AvailabilityMerge
{
    public static IReadOnlyList<TimeRange> Merge(IEnumerable<TimeRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        List<TimeRange> ordered = [.. ranges];
        ordered.Sort(static (left, right) =>
        {
            int byStart = left.Start.CompareTo(right.Start);
            return byStart != 0 ? byStart : left.End.CompareTo(right.End);
        });

        List<TimeRange> merged = [];
        foreach (TimeRange range in ordered)
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
                continue;
            }

            TimeRange current = merged[^1];
            if (range.Start < current.End)
            {
                merged[^1] = new TimeRange(current.Start, range.End > current.End ? range.End : current.End);
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }
}
