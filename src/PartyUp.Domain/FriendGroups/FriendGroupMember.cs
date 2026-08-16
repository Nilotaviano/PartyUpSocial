using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.FriendGroups;

/// <summary>
/// One roster row per (group, user), excluding the owner who is an implicit member with no row
/// (D-198/D-199). Decline, revoke, leave, and remove delete the row outright — no tombstones (D-200).
/// </summary>
public sealed class FriendGroupMember
{
    private FriendGroupMember()
    {
    }

    private FriendGroupMember(UserId userId, FriendGroupMemberStatus status, Instant invitedAt, Instant? acceptedAt)
    {
        UserId = userId;
        Status = status;
        InvitedAt = invitedAt;
        AcceptedAt = acceptedAt;
    }

    public UserId UserId { get; private set; }

    public FriendGroupMemberStatus Status { get; private set; }

    public Instant InvitedAt { get; private set; }

    public Instant? AcceptedAt { get; private set; }

    internal static FriendGroupMember Invite(UserId userId, Instant invitedAt) =>
        new(userId, FriendGroupMemberStatus.Invited, invitedAt, acceptedAt: null);

    internal void Accept(Instant acceptedAt)
    {
        Status = FriendGroupMemberStatus.Accepted;
        AcceptedAt = acceptedAt;
    }
}
