using PartyUp.Application.Abstractions;
using PartyUp.Application.FriendGroups;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Sessions;

/// <summary>
/// The registered-viewer session reads (PHASE4_DESIGN.md §5.1/§5.2). <c>GetSessionDetail</c> resolves
/// visibility over the single loaded aggregate; the two lists push viewer scoping into
/// <see cref="ISessionReadRepository"/>'s SQL rather than filtering after paging, so a page never
/// shrinks below the requested size because invisible rows were dropped afterward (the S1 carried
/// verifier note).
/// </summary>
public sealed class SessionReadUseCases(
    ISessionRepository sessions,
    ISessionReadRepository reads,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    IFriendGroupRepository groups,
    INotificationMuteRepository mutes)
{
    public async Task<SessionDetail?> GetSessionDetailAsync(
        SessionId sessionId,
        UserId viewerId,
        CancellationToken cancellationToken)
    {
        Session? session = await sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        Viewer viewer = await BuildViewerAsync(viewerId, session, cancellationToken);
        VisibilityResult visibility = VisibilityEngine.ResolveSession(session, viewer);
        if (!visibility.IsVisible)
        {
            return null;
        }

        SessionDetail? detail = await reads.FindDetailAsync(sessionId, cancellationToken);
        if (detail is null)
        {
            return null;
        }

        bool muted = await mutes.ExistsForTargetAsync(viewerId, sessionId, null, cancellationToken);
        GroupProvenance? viaGroup = await ResolveProvenanceAsync(session, viewer, cancellationToken);

        return MaskBlockedPairRoster(detail, viewer) with
        {
            Muted = muted,
            Visibility = session.Visibility,
            ViaGroup = viaGroup,
        };
    }

    /// <summary>
    /// D-206: the "via &lt;group&gt;" label's source, membership-based (foreman ruling) — the tag
    /// answers "which targeted group is this viewer in", independent of which rule actually resolved
    /// the viewer's access. <see cref="Viewer.GroupIds"/> is already the intersection of the viewer's
    /// owned/Accepted groups with this target's audience (<c>ViewerGroups.GrantingAsync</c>), so any
    /// id in it already carries the membership proof; the first one in the target's own audience order
    /// is the answer, matching the list page's <c>GroupProvenanceReader</c> tie-break. A member who also
    /// resolves via rule 1 (organizer/participant) still gets the tag here, unlike the old grant-based
    /// read.
    /// </summary>
    private async Task<GroupProvenance?> ResolveProvenanceAsync(
        Session session,
        Viewer viewer,
        CancellationToken cancellationToken)
    {
        if (viewer.GroupIds.Count == 0)
        {
            return null;
        }

        bool found = false;
        FriendGroupId resolvedGroupId = default;
        foreach (FriendGroupId candidate in session.AudienceGroupIds)
        {
            if (viewer.GroupIds.Contains(candidate))
            {
                resolvedGroupId = candidate;
                found = true;
                break;
            }
        }

        if (!found)
        {
            return null;
        }

        GroupName? name = await groups.FindNameAsync(resolvedGroupId, cancellationToken);
        return name is null ? null : new GroupProvenance(resolvedGroupId, name);
    }

    /// <summary>
    /// PHASE5_DESIGN.md §5.1 (D-076, closing the live prod leak this slice fixes): rule 4 tests only
    /// the (viewer, organizer) pair, so a third-party session's roster can legitimately still carry a
    /// participant the viewer is blocked with (A-077 (e)). <c>viewer.BlockedCounterpartIds</c> is
    /// already every counterpart on this session blocked with the viewer in either direction
    /// (<see cref="BuildViewerAsync"/>'s <c>IBlockRepository.FindPairsAmongAsync</c> call — no new
    /// port member). This sets <see cref="RosterEntry.MaskedForViewer"/> and nulls
    /// <see cref="RosterEntry.DisplayName"/> — the wire shape's "displayName omitted and userId
    /// omitted" (§5.1) needs both signals available to it, and <see cref="RosterEntry.MaskedForViewer"/>
    /// is what keeps this distinct from the tombstone null-name rule (D-092, which nulls the name for
    /// every viewer and never sets the flag). The underlying <see cref="ParticipantRef"/> stays real —
    /// occupancy, ordering and the deletion executor all still see the true participant.
    /// </summary>
    private static SessionDetail MaskBlockedPairRoster(SessionDetail detail, Viewer viewer)
    {
        if (viewer.BlockedCounterpartIds.Count == 0)
        {
            return detail;
        }

        List<RosterEntry> roster = [.. detail.Roster.Select(entry =>
            entry.Participant.UserId is { } userId && viewer.BlockedCounterpartIds.Contains(userId)
                ? entry with { MaskedForViewer = true, DisplayName = null, Avatar = null }
                : entry)];

        return detail with { Roster = roster };
    }

    public Task<SessionPage<SessionSummary>> ListUpcomingSessionsAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        reads.ListUpcomingAsync(viewerId, cursor, pageSize, cancellationToken);

    public Task<SessionPage<SessionSummary>> ListCompletedHistoryAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        reads.ListHistoryAsync(viewerId, cursor, pageSize, cancellationToken);

    /// <summary>
    /// Builds the minimal §7 viewer facts this one session needs: the organizer plus every other
    /// registered participant, read as two bulk (not per-counterpart) queries — proportionate for a
    /// single-session read, and never a row lock: a GET must take none (F6). The previous
    /// implementation looped <see cref="IBlockRepository.FindPairForUpdateAsync"/> per counterpart,
    /// which takes <c>FOR UPDATE</c> on a read path; this uses the non-locking bulk read instead.
    /// </summary>
    private async Task<Viewer> BuildViewerAsync(UserId viewerId, Session session, CancellationToken cancellationToken)
    {
        HashSet<UserId> counterparts = [session.OrganizerId];
        foreach (Participation participation in session.Participations)
        {
            if (participation.UserId is { } userId && userId != viewerId)
            {
                counterparts.Add(userId);
            }
        }

        counterparts.Remove(viewerId);

        // §7 rule 2g (D-198): the audience subset this viewer is in. Unlocked — a GET takes no row
        // locks (F6) and nothing is committed against the answer.
        IReadOnlyCollection<FriendGroupId> viewerGroupIds = await ViewerGroups.GrantingAsync(
            groups, viewerId, session.Visibility, session.AudienceGroupIds, cancellationToken);

        if (counterparts.Count == 0)
        {
            return Viewer.Create(viewerId, null, null, viewerGroupIds);
        }

        IReadOnlyList<Friendship> pairFriendships = await friendships.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);
        IReadOnlyList<Block> pairBlocks = await blocks.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);

        List<UserId> friendIds = [.. pairFriendships.Where(f => f.IsAccepted).Select(f => f.Counterpart(viewerId))];
        List<UserId> blockedIds = [.. pairBlocks.Select(b => b.BlockerId == viewerId ? b.BlockedId : b.BlockerId)];

        return Viewer.Create(viewerId, friendIds, blockedIds, viewerGroupIds);
    }
}
