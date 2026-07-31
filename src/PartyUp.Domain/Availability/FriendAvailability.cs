using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>One selected friend's merged availability, the overlap engine's per-friend input (D-100).</summary>
public sealed record FriendAvailability(UserId FriendId, IReadOnlyList<TimeRange> Blocks)
{
    public static FriendAvailability Create(UserId friendId, IEnumerable<TimeRange> blocks) =>
        new(friendId, AvailabilityMerge.Merge(blocks));
}
