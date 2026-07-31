using NodaTime;

namespace PartyUp.Domain.Kernel;

/// <summary>
/// A half-open interval <c>[Start, End)</c> (DOMAIN_MODEL §1). Named <c>TimeRange</c> rather than
/// <c>Interval</c> because every domain file also imports <c>NodaTime</c>, which owns that name.
/// </summary>
public readonly record struct TimeRange
{
    public TimeRange(Instant start, Instant end)
    {
        if (end <= start)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"A time range must end after it starts, but was [{start}, {end})."));
        }

        Start = start;
        End = end;
    }

    public Instant Start { get; }

    public Instant End { get; }

    public Duration Length => End - Start;

    public static TimeRange FromDuration(Instant start, Duration length) => new(start, start + length);

    /// <summary>Half-open: the end instant is not contained.</summary>
    public bool Contains(Instant instant) => instant >= Start && instant < End;

    /// <summary>Half-open: abutting ranges do not overlap.</summary>
    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    public bool Abuts(TimeRange other) => End == other.Start || other.End == Start;

    public bool Encloses(TimeRange other) => Start <= other.Start && other.End <= End;

    public bool TryIntersect(TimeRange other, out TimeRange intersection)
    {
        Instant start = Instant.Max(Start, other.Start);
        Instant end = Instant.Min(End, other.End);
        if (end <= start)
        {
            intersection = default;
            return false;
        }

        intersection = new TimeRange(start, end);
        return true;
    }

    public override string ToString() => FormattableString.Invariant($"[{Start}, {End})");
}
