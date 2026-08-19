using PartyUp.Application.Polls;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The friend-group aggregate's persistence port (D-198/D-199). Every locking read follows the
/// graph's declared order: the caller locks the owner <c>users</c> row first where the owned-group
/// cap is at stake, and membership rows lock after their group row.
/// </summary>
public interface IFriendGroupRepository
{
    void Add(FriendGroup group);

    void Remove(FriendGroup group);

    Task<FriendGroup?> FindNoTrackingAsync(FriendGroupId id, CancellationToken cancellationToken);

    /// <summary><c>FOR NO KEY UPDATE</c> on the group row, then the tracked aggregate with its roster.</summary>
    Task<FriendGroup?> LoadForUpdateAsync(FriendGroupId id, CancellationToken cancellationToken);

    /// <summary>D-208's owned-group cap read; the caller has already locked the owner's user row.</summary>
    Task<int> CountOwnedForUpdateAsync(UserId ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// The gate step (D-198): the ids among <paramref name="candidateGroupIds"/> where the viewer is
    /// the owner or holds an <c>Accepted</c> row, with those group and membership rows locked, so a
    /// concurrent audience or roster edit cannot make the answer stale between resolve and commit.
    /// </summary>
    Task<IReadOnlyCollection<FriendGroupId>> LockViewerGroupIdsAsync(
        UserId viewerId,
        IReadOnlyCollection<FriendGroupId> candidateGroupIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// The read-path twin of <see cref="LockViewerGroupIdsAsync"/>: the same membership answer with no
    /// row locks, because a GET must take none (PHASE5 F6). Safe there precisely because a read
    /// resolves and returns in one shot — nothing is committed against the answer.
    /// </summary>
    Task<IReadOnlyCollection<FriendGroupId>> GetViewerGroupIdsAsync(
        UserId viewerId,
        IReadOnlyCollection<FriendGroupId> candidateGroupIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// D-206: the live name of a group whose membership the caller has ALREADY established for the
    /// viewer (the engine's <c>VisibilityGrant.Group</c> result). Reading the name is not itself an
    /// authorization — callers must not reach here for a group the viewer is not in.
    /// </summary>
    Task<GroupName?> FindNameAsync(FriendGroupId id, CancellationToken cancellationToken);

    /// <summary>Every group the user owns or holds a row in — <c>Invited</c> rows included.</summary>
    Task<IReadOnlyCollection<FriendGroup>> ListInvolvingAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>
    /// D-210: every group <paramref name="ownerId"/> owns that holds an <c>Accepted</c> row for
    /// <paramref name="memberId"/>. Scoped to owned groups by the query itself, so the answer can
    /// never name a group the caller is not the owner of.
    /// </summary>
    Task<IReadOnlyCollection<FriendGroup>> ListOwnedContainingAsync(
        UserId ownerId,
        UserId memberId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Bulk name+avatar resolution for a group's owner + roster, mirroring
    /// <c>IPollReadRepository.FindNameplatesAsync</c> exactly: one prefetch, both fields, so no read
    /// path can resolve one without the other.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, UserNameplate>> FindNameplatesAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// D-205's deletion-time read: the distinct organizer of every session and poll whose audience
    /// currently targets <paramref name="groupId"/> — the recipient set for the one
    /// <c>AudienceGroupDeleted</c> notification a deletion produces per affected organizer. Must be
    /// read before the caller calls <see cref="Remove"/>, in the same transaction as the group's own
    /// <see cref="LoadForUpdateAsync"/> lock.
    /// </summary>
    Task<IReadOnlyCollection<UserId>> ListDistinctOrganizersTargetingAsync(
        FriendGroupId groupId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Thrown by <see cref="IUnitOfWork.ExecuteAsync{T}"/> when a friend group's create/rename insert
/// loses the race against <c>ux_friend_groups_owner_name</c> (D-199's per-owner, case-insensitive name
/// space) — the same treatment <c>FriendshipPairUniqueConflictException</c> gets for
/// <c>ux_friendships_pair</c>.
/// </summary>
public sealed class FriendGroupNameConflictException : Exception
{
    public FriendGroupNameConflictException(Exception innerException)
        : base(nameof(FriendGroupNameConflictException), innerException)
    {
    }
}
