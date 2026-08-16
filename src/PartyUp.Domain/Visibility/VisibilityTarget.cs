using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Visibility;

/// <summary>
/// A session or a poll reduced to what §7 evaluates: the organizer, the audience level and its
/// targeted friend groups, the registered members' two rule facts, and the users holding a Pending
/// join request. Sessions and polls differ only in how those facts are computed
/// (<see cref="SessionVisibility"/>, <see cref="PollVisibility"/>), so the engine has one shape to
/// reason about.
/// </summary>
public sealed class VisibilityTarget
{
    private readonly List<VisibilityMember> _members;
    private readonly HashSet<UserId> _pendingRequesterIds;
    private readonly List<FriendGroupId> _audienceGroupIds;

    private VisibilityTarget(
        UserId organizerId,
        ContentVisibility visibility,
        List<FriendGroupId> audienceGroupIds,
        List<VisibilityMember> members,
        HashSet<UserId> pendingRequesterIds)
    {
        OrganizerId = organizerId;
        Visibility = visibility;
        _audienceGroupIds = audienceGroupIds;
        _members = members;
        _pendingRequesterIds = pendingRequesterIds;
    }

    public UserId OrganizerId { get; }

    /// <summary>§7 level gate: Private is rule 1 only, Groups is rule 2g, Public is rule 2.</summary>
    public ContentVisibility Visibility { get; }

    /// <summary>D-201: the groups a <c>Groups</c> target is shared with; empty degrades to rule 1 (D-205).</summary>
    public IReadOnlyCollection<FriendGroupId> AudienceGroupIds => _audienceGroupIds;

    public IReadOnlyList<VisibilityMember> Members => _members;

    /// <summary>D-043's "outstanding" requests; rejected, withdrawn and lapsed ones grant nothing.</summary>
    public IReadOnlyCollection<UserId> PendingRequesterIds => _pendingRequesterIds;

    public static VisibilityTarget Create(
        UserId organizerId,
        ContentVisibility visibility,
        IEnumerable<FriendGroupId> audienceGroupIds,
        IEnumerable<VisibilityMember> members,
        IEnumerable<UserId> pendingRequesterIds)
    {
        ArgumentNullException.ThrowIfNull(audienceGroupIds);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(pendingRequesterIds);

        return new VisibilityTarget(
            organizerId,
            visibility,
            [.. audienceGroupIds],
            [.. members],
            [.. pendingRequesterIds]);
    }

    public bool HasPendingRequestFrom(UserId userId) => _pendingRequesterIds.Contains(userId);

    public bool TryFindMember(UserId userId, out VisibilityMember member)
    {
        foreach (VisibilityMember candidate in _members)
        {
            if (candidate.UserId == userId)
            {
                member = candidate;
                return true;
            }
        }

        member = default;
        return false;
    }
}
