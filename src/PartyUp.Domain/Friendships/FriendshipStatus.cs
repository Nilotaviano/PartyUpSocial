namespace PartyUp.Domain.Friendships;

// Values are persisted: append only, never renumber.

/// <summary>
/// D-015/D-105: rejection and removal delete the row, so the only stored states are the two a live
/// friendship can be in (DOMAIN_MODEL §4).
/// </summary>
public enum FriendshipStatus
{
    Pending = 0,
    Accepted = 1,
}
