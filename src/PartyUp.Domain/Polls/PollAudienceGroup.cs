using PartyUp.Domain.FriendGroups;

namespace PartyUp.Domain.Polls;

/// <summary>
/// One row of a <c>Groups</c>-level poll's audience (D-203), copied onto the session finalization
/// creates (D-204). It carries nothing but the targeted group's id: the aggregate's public surface
/// stays a collection of <see cref="FriendGroupId"/>. A reference type because the audience is a
/// junction table and an entity type can never be a value type.
/// </summary>
public sealed class PollAudienceGroup
{
    private PollAudienceGroup()
    {
    }

    internal PollAudienceGroup(FriendGroupId groupId) => GroupId = groupId;

    public FriendGroupId GroupId { get; private set; }
}
