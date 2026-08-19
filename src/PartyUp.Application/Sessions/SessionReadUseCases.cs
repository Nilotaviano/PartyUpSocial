using PartyUp.Application.Abstractions;
using PartyUp.Application.FriendGroups;
using PartyUp.Application.Polls;
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
        IReadOnlyList<AudienceGroupEntry>? audienceGroups = viewerId == session.OrganizerId
            ? await ResolveAudienceGroupsAsync(session, viewer, cancellationToken)
            : null;

        return MaskBlockedPairRoster(detail, viewer) with
        {
            Muted = muted,
            Visibility = session.Visibility,
            ViaGroup = viaGroup,
            AudienceGroups = audienceGroups,
        };
    }

    /// <summary>
    /// D-206 edit-prefill: the organizer's own audience data, id-complete regardless of current
    /// membership (D-201/D-203 retained targeting). The name is resolved only for ids
    /// <see cref="Viewer.GroupIds"/> already proves owner/Accepted membership for — that set is the
    /// intersection <c>BuildViewerAsync</c> computed for this same organizer against this same
    /// session's audience, so no separate membership query is needed here; a group outside it gets a
    /// null name rather than a lookup, matching <see cref="ResolveProvenanceAsync"/>'s guard.
    /// </summary>
    private async Task<IReadOnlyList<AudienceGroupEntry>?> ResolveAudienceGroupsAsync(
        Session session,
        Viewer viewer,
        CancellationToken cancellationToken)
    {
        if (session.Visibility != ContentVisibility.Groups || session.AudienceGroupIds.Count == 0)
        {
            return null;
        }

        List<AudienceGroupEntry> entries = [];
        foreach (FriendGroupId groupId in session.AudienceGroupIds)
        {
            GroupName? name = viewer.GroupIds.Contains(groupId)
                ? await groups.FindNameAsync(groupId, cancellationToken)
                : null;
            entries.Add(new AudienceGroupEntry(groupId, name));
        }

        return entries;
    }

    /// <summary>
    /// D-206: the "via &lt;group&gt;" label's source is membership, not the resolved grant — the tag
    /// answers "which targeted group is this viewer in", so a member who also resolves via rule 1
    /// (organizer/participant) still gets it. <see cref="Viewer.GroupIds"/> is already the
    /// intersection of the viewer's owned/Accepted groups with this target's audience
    /// (<c>ViewerGroups.GrantingAsync</c>), so any id in it carries the membership proof; the first
    /// one in the target's own audience order is the answer, matching the list page's
    /// <c>GroupProvenanceReader</c> tie-break.
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

    /// <summary>
    /// D-221: the repository decides membership of the page; this decides who on it may be read.
    /// Names match <c>GET /sessions/{id}</c>'s roster: a player the viewer actually played with keeps
    /// their name, and only a blocked pair loses it (D-076, which drops both channels). Avatars are
    /// narrower than the detail page — the detail roster hands avatars to any co-participant, while
    /// history gates them on the friendship channel only (D-188).
    /// </summary>
    public async Task<SessionPage<SessionSummary>> ListCompletedHistoryAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        SessionPage<SessionSummary> page = await reads.ListHistoryAsync(viewerId, cursor, pageSize, cancellationToken);

        HashSet<UserId> everyone = [.. page.Items
            .SelectMany(summary => summary.Players ?? [])
            .Select(player => player.UserId)];
        if (everyone.Count == 0)
        {
            return page;
        }

        ViewerLegibility legibility = await ResolveLegibilityAsync(viewerId, everyone, cancellationToken);

        List<SessionSummary> items = [.. page.Items.Select(summary => summary.Players is { } players
            ? summary with { Players = [.. players.Select(legibility.Mask)] }
            : summary)];

        return new SessionPage<SessionSummary>(items, page.NextCursor);
    }

    /// <summary>
    /// D-222: the recap is computed over the same Completed arm the history list uses. A top mate is
    /// by construction a co-participant of every session they were counted on, so their identity
    /// travels on exactly the terms a history player's does.
    /// </summary>
    public async Task<SessionHistoryStats> GetHistoryStatsAsync(
        UserId viewerId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CompletedSessionFact> facts =
            await reads.ListCompletedHistoryFactsAsync(viewerId, cancellationToken);
        SessionHistoryStats stats = SessionHistoryStatsCalculator.Compute(facts);

        if (stats.TopMates.Count == 0)
        {
            return stats;
        }

        HashSet<UserId> mateIds = [.. stats.TopMates.Select(mate => mate.Player.UserId)];
        ViewerLegibility legibility = await ResolveLegibilityAsync(viewerId, mateIds, cancellationToken);
        IReadOnlyDictionary<Guid, UserNameplate> nameplates =
            await reads.FindNameplatesAsync(mateIds, cancellationToken);

        List<HistoryTopMate> mates = [.. stats.TopMates.Select(mate =>
        {
            UserNameplate? nameplate = nameplates.GetValueOrDefault(mate.Player.UserId.Value);
            return mate with
            {
                Player = legibility.Mask(
                    new HistoryPlayer(mate.Player.UserId, nameplate?.DisplayName, nameplate?.Avatar)),
            };
        })];

        return stats with { TopMates = mates };
    }

    /// <summary>
    /// The two identity channels of one co-participant, decided in one place. The name follows the
    /// detail roster (<see cref="MaskBlockedPairRoster"/>): a person the viewer shared a session with
    /// is readable, and only a blocked pair is not (D-076, which drops the avatar with it). The
    /// avatar is the narrower channel — it needs the friend-set resolution D-188/AVATARS_DESIGN.md
    /// §2.4 give every list surface, so a legible-but-unfriended person travels as a name alone.
    /// </summary>
    private readonly record struct ViewerLegibility(HashSet<UserId> Blocked, HashSet<UserId> AvatarReadable)
    {
        internal HistoryPlayer Mask(HistoryPlayer player) => Blocked.Contains(player.UserId)
            ? new HistoryPlayer(player.UserId)
            : AvatarReadable.Contains(player.UserId) ? player : player with { Avatar = null };
    }

    /// <summary>
    /// Two bulk, non-locking pair reads over <paramref name="counterparts"/> — never one query per
    /// person: the blocked pairs (either direction) and the Accepted friendships whose avatars the
    /// viewer may resolve. The viewer themselves is always avatar-readable.
    /// </summary>
    private async Task<ViewerLegibility> ResolveLegibilityAsync(
        UserId viewerId,
        HashSet<UserId> counterparts,
        CancellationToken cancellationToken)
    {
        HashSet<UserId> others = [.. counterparts];
        others.Remove(viewerId);

        HashSet<UserId> avatarReadable = [viewerId];
        if (others.Count == 0)
        {
            return new ViewerLegibility([], avatarReadable);
        }

        IReadOnlyList<Friendship> pairFriendships =
            await friendships.FindPairsAmongAsync(viewerId, others, cancellationToken);
        IReadOnlyList<Block> pairBlocks = await blocks.FindPairsAmongAsync(viewerId, others, cancellationToken);

        HashSet<UserId> blocked = [.. pairBlocks.Select(b => b.BlockerId == viewerId ? b.BlockedId : b.BlockerId)];
        foreach (Friendship friendship in pairFriendships)
        {
            UserId counterpart = friendship.Counterpart(viewerId);
            if (friendship.IsAccepted && !blocked.Contains(counterpart))
            {
                avatarReadable.Add(counterpart);
            }
        }

        return new ViewerLegibility(blocked, avatarReadable);
    }

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
