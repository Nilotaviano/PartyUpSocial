using PartyUp.Domain.FriendGroups;

namespace PartyUp.Domain.Sessions;

/// <summary>
/// One row of a <c>Groups</c>-level session's audience (D-203). It carries nothing but the targeted
/// group's id: the aggregate's public surface stays a collection of
/// <see cref="FriendGroupId"/>. A reference type because the audience is a junction table and an
/// entity type can never be a value type.
/// </summary>
public sealed class SessionAudienceGroup
{
    private SessionAudienceGroup()
    {
    }

    internal SessionAudienceGroup(FriendGroupId groupId) => GroupId = groupId;

    public FriendGroupId GroupId { get; private set; }
}
