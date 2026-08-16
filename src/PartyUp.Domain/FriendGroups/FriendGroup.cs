using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.FriendGroups;

/// <summary>
/// Shared, owner-managed aggregate (D-199). The owner is an implicit member with no row — every
/// row in <see cref="Members"/> is someone else. Invite/accept/decline/revoke/leave/remove all use
/// the row-deletion model of Friendship, no tombstones (D-200).
/// </summary>
public sealed class FriendGroup : AggregateRoot
{
    /// <summary>D-208: membership rows per group (Invited + Accepted); the owner is never counted.</summary>
    public const int MaxMembers = 100;

    private readonly List<FriendGroupMember> _members = [];

    private FriendGroup()
    {
    }

    private FriendGroup(FriendGroupId id, UserId ownerId, GroupName name, Instant createdAt)
    {
        Id = id;
        OwnerId = ownerId;
        Name = name;
        CreatedAt = createdAt;
    }

    public FriendGroupId Id { get; private set; }

    public UserId OwnerId { get; private set; }

    public GroupName Name { get; private set; } = null!;

    public Instant CreatedAt { get; private set; }

    /// <summary>Excludes the owner — the owner is implicit (D-198).</summary>
    public IReadOnlyList<FriendGroupMember> Members => _members;

    public static FriendGroup Create(UserId ownerId, GroupName name, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(clock);

        return new FriendGroup(FriendGroupId.New(), ownerId, name, clock.GetCurrentInstant());
    }

    public void Rename(UserId actor, GroupName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        RequireOwner(actor);

        Name = name;
    }

    /// <summary>
    /// Guard order is normative (D-199/D-200/D-202/D-208): actor-not-owner → Forbidden;
    /// invitee is the owner → ValidationFailed; not a currently-accepted friend of the owner →
    /// NotFound with the identical message a stranger gets (the caller collapses blocked pairs
    /// into false here — one indistinguishable error, D-202); duplicate row → Duplicate; the
    /// 100-row cap → LimitExceeded; then add the row and raise the event.
    /// </summary>
    public void Invite(UserId actor, UserId invitee, bool isAcceptedFriendOfOwner, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        RequireOwner(actor);

        if (invitee == OwnerId)
        {
            throw new DomainException(DomainError.ValidationFailed, "The owner is already part of their own group.");
        }

        if (!isAcceptedFriendOfOwner)
        {
            throw new DomainException(DomainError.NotFound, "No such user.");
        }

        if (_members.Any(m => m.UserId == invitee))
        {
            throw new DomainException(DomainError.Duplicate, "This user is already in the group.");
        }

        if (_members.Count >= MaxMembers)
        {
            throw new DomainException(
                DomainError.LimitExceeded,
                FormattableString.Invariant($"A friend group can have at most {MaxMembers} members."));
        }

        _members.Add(FriendGroupMember.Invite(invitee, clock.GetCurrentInstant()));

        Raise(new FriendGroupInviteSent(Id, OwnerId, invitee));
    }

    public void AcceptInvite(UserId invitee, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        FriendGroupMember member = FindInvited(invitee);

        member.Accept(clock.GetCurrentInstant());

        Raise(new FriendGroupInviteAccepted(Id, OwnerId, invitee));
    }

    public void DeclineInvite(UserId invitee)
    {
        FindInvited(invitee);
        _members.RemoveAll(m => m.UserId == invitee);
    }

    public void RevokeInvite(UserId actor, UserId invitee)
    {
        RequireOwner(actor);
        FindInvited(invitee);
        _members.RemoveAll(m => m.UserId == invitee);
    }

    public void RemoveMember(UserId actor, UserId member)
    {
        RequireOwner(actor);
        RequireMemberRow(member);
        _members.RemoveAll(m => m.UserId == member);
    }

    /// <summary>The owner is implicit and cannot leave their own group (D-198).</summary>
    public void Leave(UserId member)
    {
        if (member == OwnerId)
        {
            throw new DomainException(DomainError.InvalidTransition, "The owner cannot leave their own group.");
        }

        RequireMemberRow(member);
        _members.RemoveAll(m => m.UserId == member);
    }

    /// <summary>
    /// Block-effects hook (D-202): when one side of the block is the owner, delete the other
    /// party's row whatever its status — Invited or Accepted — silently, with no event.
    /// A no-op for member↔member blocks (neither is the owner).
    /// </summary>
    public void RemoveCounterpartOf(UserId blockerOrOwner, UserId other)
    {
        if (blockerOrOwner != OwnerId && other != OwnerId)
        {
            return;
        }

        UserId target = blockerOrOwner == OwnerId ? other : blockerOrOwner;
        _members.RemoveAll(m => m.UserId == target);
    }

    /// <summary>True for the owner too, even though the owner has no row (D-198).</summary>
    public bool HasAcceptedMember(UserId userId) =>
        userId == OwnerId || _members.Any(m => m.UserId == userId && m.Status == FriendGroupMemberStatus.Accepted);

    public bool Involves(UserId userId) => userId == OwnerId || _members.Any(m => m.UserId == userId);

    private void RequireOwner(UserId actor)
    {
        if (actor != OwnerId)
        {
            throw new DomainException(DomainError.Forbidden, "Only the group's owner can do this.");
        }
    }

    private FriendGroupMember FindInvited(UserId userId)
    {
        FriendGroupMember? member = _members.Find(m => m.UserId == userId && m.Status == FriendGroupMemberStatus.Invited);

        return member ?? throw new DomainException(DomainError.NotFound, "No pending invite for this user.");
    }

    private void RequireMemberRow(UserId userId)
    {
        if (!_members.Any(m => m.UserId == userId))
        {
            throw new DomainException(DomainError.NotFound, "This user is not a member of the group.");
        }
    }
}
