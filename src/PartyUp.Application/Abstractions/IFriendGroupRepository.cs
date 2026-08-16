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

    /// <summary>Every group the user owns or holds a row in — <c>Invited</c> rows included.</summary>
    Task<IReadOnlyCollection<FriendGroup>> ListInvolvingAsync(UserId userId, CancellationToken cancellationToken);
}
