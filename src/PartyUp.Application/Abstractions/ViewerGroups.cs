using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The §7 rule-2g input every session/poll visibility resolve needs (D-198): the subset of the
/// target's audience the viewer owns or holds an <c>Accepted</c> row in. Kept in one place because
/// the rule is a security boundary — a caller that forgets it silently downgrades a member to
/// "no access" (fail-closed, so never a leak, but a real regression), and a caller that resolves it
/// too early opens the stale-audience race the design forbids.
/// </summary>
internal static class ViewerGroups
{
    /// <summary>
    /// Write paths: resolved <b>after</b> the parent row lock so a concurrent audience or roster edit
    /// cannot make the answer stale between the resolve and the commit (design §3).
    /// </summary>
    public static async Task<IReadOnlyCollection<FriendGroupId>> LockGrantingAsync(
        IFriendGroupRepository groups,
        UserId viewerId,
        ContentVisibility visibility,
        IReadOnlyCollection<FriendGroupId> audienceGroupIds,
        CancellationToken cancellationToken) =>
        visibility == ContentVisibility.Groups && audienceGroupIds.Count > 0
            ? await groups.LockViewerGroupIdsAsync(viewerId, audienceGroupIds, cancellationToken)
            : [];

    /// <summary>Read paths: the same answer without row locks — a GET takes none (F6).</summary>
    public static async Task<IReadOnlyCollection<FriendGroupId>> GrantingAsync(
        IFriendGroupRepository groups,
        UserId viewerId,
        ContentVisibility visibility,
        IReadOnlyCollection<FriendGroupId> audienceGroupIds,
        CancellationToken cancellationToken) =>
        visibility == ContentVisibility.Groups && audienceGroupIds.Count > 0
            ? await groups.GetViewerGroupIdsAsync(viewerId, audienceGroupIds, cancellationToken)
            : [];

    /// <summary>
    /// Re-issues <paramref name="viewer"/> carrying <paramref name="groupIds"/>. The relation sets are
    /// reused verbatim, so <see cref="Viewer.From"/> stays the single place friendship/block rows are
    /// interpreted.
    /// </summary>
    public static Viewer With(Viewer viewer, IReadOnlyCollection<FriendGroupId> groupIds) =>
        groupIds.Count == 0
            ? viewer
            : Viewer.Create(viewer.Id, viewer.AcceptedFriendIds, viewer.BlockedCounterpartIds, groupIds);
}
