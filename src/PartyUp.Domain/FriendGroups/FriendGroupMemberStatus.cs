namespace PartyUp.Domain.FriendGroups;

// Values are persisted: append only, never renumber.

/// <summary>
/// D-200: decline, revoke, leave, and remove all delete the row — the only stored states are the
/// two a live membership can be in, same row model as Friendship (DOMAIN_MODEL §4).
/// </summary>
public enum FriendGroupMemberStatus
{
    Invited = 0,
    Accepted = 1,
}
