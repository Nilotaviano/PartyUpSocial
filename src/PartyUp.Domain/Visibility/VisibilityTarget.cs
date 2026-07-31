using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Visibility;

/// <summary>
/// A session or a poll reduced to what §7 evaluates: the organizer, the privacy flag, the
/// registered members' two rule facts, and the users holding a Pending join request. Sessions and
/// polls differ only in how those facts are computed (<see cref="SessionVisibility"/>,
/// <see cref="PollVisibility"/>), so the engine has one shape to reason about.
/// </summary>
public sealed class VisibilityTarget
{
    private readonly List<VisibilityMember> _members;
    private readonly HashSet<UserId> _pendingRequesterIds;

    private VisibilityTarget(
        UserId organizerId,
        bool isPrivate,
        List<VisibilityMember> members,
        HashSet<UserId> pendingRequesterIds)
    {
        OrganizerId = organizerId;
        IsPrivate = isPrivate;
        _members = members;
        _pendingRequesterIds = pendingRequesterIds;
    }

    public UserId OrganizerId { get; }

    /// <summary>§7 rule 3: a private target is rule 1 only.</summary>
    public bool IsPrivate { get; }

    public IReadOnlyList<VisibilityMember> Members => _members;

    /// <summary>D-043's "outstanding" requests; rejected, withdrawn and lapsed ones grant nothing.</summary>
    public IReadOnlyCollection<UserId> PendingRequesterIds => _pendingRequesterIds;

    public static VisibilityTarget Create(
        UserId organizerId,
        bool isPrivate,
        IEnumerable<VisibilityMember> members,
        IEnumerable<UserId> pendingRequesterIds)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(pendingRequesterIds);

        return new VisibilityTarget(organizerId, isPrivate, [.. members], [.. pendingRequesterIds]);
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
