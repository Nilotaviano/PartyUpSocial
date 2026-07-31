using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>
/// A subset-maximal overlap window (D-035/D-052/§12-28). <see cref="Available"/> are the friends
/// available for all of <see cref="Range"/>; <see cref="Maybe"/> are those available for part of it
/// — half-open, so merely touching an endpoint is not availability.
/// </summary>
public sealed record OverlapWindow(TimeRange Range, IReadOnlyList<UserId> Available, IReadOnlyList<UserId> Maybe)
{
    public int AvailableCount => Available.Count;

    public int MaybeCount => Maybe.Count;

    public Duration Length => Range.Length;
}
