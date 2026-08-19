using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.FriendGroups;
using PartyUp.Application.Polls;
using PartyUp.Application.Sessions;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The read-only Session projections (PHASE4_DESIGN.md §5.1, PHASE5_DESIGN.md §5.1/A-077/A-080).
/// Every query is <c>AsNoTracking</c> and applies no visibility or identity masking of its own — that
/// is layered on by the caller. <c>ViewerRelation</c> and organizer display-name resolution
/// (PHASE5_DESIGN.md §5.1) are computed once, in <see cref="BuildSummariesAsync"/>, and shared by
/// every summary-producing surface.
/// </summary>
public sealed class SessionReadRepository(AppDbContext context) : ISessionReadRepository
{
    /// <summary>
    /// D-221's attachment test as a composable SQL fragment, in the same positional style as
    /// <see cref="SessionVisibilitySql"/>: <c>{0}</c> is the viewer's id and <c>{1}</c> is
    /// <see cref="ParticipationStatus.Going"/>, so every caller must bind those two arguments in that
    /// order. It is a membership test, never a status re-derivation — the session's own materialized
    /// status is filtered separately by the caller.
    /// </summary>
    private const string TookPartSql = """
        (
            s.organizer_id = {0}
            OR EXISTS (
                SELECT 1 FROM session_participations sp
                WHERE sp.session_id = s.id AND sp.user_id = {0}
                  AND sp.status = {1} AND sp.is_waitlisted = false
            )
        )
        """;

    public async Task<SessionDetail?> FindDetailAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        var row = await context.Sessions
            .AsNoTracking()
            .Include(session => session.Participations)
            .Include(session => session.GuestIdentities)
            .Where(session => session.Id == sessionId)
            .Select(session => new
            {
                Session = session,
                Xmin = EF.Property<uint>(session, "xmin"),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return await ToDetailAsync(row.Session, row.Xmin, cancellationToken);
    }

    public async Task<SessionPage<SessionSummary>> ListUpcomingAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        short completed = (short)SessionStatus.Completed;
        short expired = (short)SessionStatus.Expired;
        short canceled = (short)SessionStatus.Canceled;
        Guid viewer = viewerId.Value;
        string predicate = SessionVisibilitySql.Predicate(SessionVisibilitySql.AccessKind.Any);

        List<Guid> ids = cursor is { } after
            ? await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM sessions s
                WHERE status NOT IN ({1}, {2}, {3})
                  AND (start_time, id) > ({4}, {5})
                  AND {{predicate}}
                ORDER BY start_time, id
                LIMIT {6}
                """, viewer, canceled, expired, completed, after.SortKey, after.Id, pageSize))
                .ToListAsync(cancellationToken)
            : await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM sessions s
                WHERE status NOT IN ({1}, {2}, {3})
                  AND {{predicate}}
                ORDER BY start_time, id
                LIMIT {4}
                """, viewer, canceled, expired, completed, pageSize))
                .ToListAsync(cancellationToken);

        List<Session> sessions = await HydrateSessionsInOrderAsync(ids, cancellationToken);
        List<SessionSummary> summaries = await BuildSummariesAsync(sessions, viewerId, cancellationToken);

        return ToSummaryPage(summaries, pageSize);
    }

    /// <summary>
    /// D-221: history is what the viewer took part in, not merely what they may see — the
    /// <see cref="TookPartSql"/> attachment test (organizer, or an active non-waitlisted <c>Going</c>
    /// row) runs alongside, not instead of, the A-077 visibility fragment. Both terminal-with-a-past
    /// statuses qualify: a Canceled session is part of the story and travels with its own status on
    /// the wire. Newest-first, tie-broken by id, cursor contract unchanged (A-072).
    /// </summary>
    public async Task<SessionPage<SessionSummary>> ListHistoryAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        short completed = (short)SessionStatus.Completed;
        short canceled = (short)SessionStatus.Canceled;
        short going = (short)ParticipationStatus.Going;
        Guid viewer = viewerId.Value;
        string predicate = SessionVisibilitySql.Predicate(SessionVisibilitySql.AccessKind.Any);

        List<Guid> ids = cursor is { } before
            ? await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM sessions s
                WHERE status IN ({2}, {3})
                  AND {{TookPartSql}}
                  AND (start_time, id) < ({4}, {5})
                  AND {{predicate}}
                ORDER BY start_time DESC, id DESC
                LIMIT {6}
                """, viewer, going, completed, canceled, before.SortKey, before.Id, pageSize))
                .ToListAsync(cancellationToken)
            : await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM sessions s
                WHERE status IN ({2}, {3})
                  AND {{TookPartSql}}
                  AND {{predicate}}
                ORDER BY start_time DESC, id DESC
                LIMIT {4}
                """, viewer, going, completed, canceled, pageSize))
                .ToListAsync(cancellationToken);

        List<Session> sessions = await HydrateSessionsInOrderAsync(ids, cancellationToken);
        List<SessionSummary> summaries = await BuildSummariesAsync(sessions, viewerId, cancellationToken);
        summaries = await AttachPlayersAsync(sessions, summaries, cancellationToken);

        return ToSummaryPage(summaries, pageSize);
    }

    /// <summary>
    /// D-222: the recap's inputs over <see cref="ListHistoryAsync"/>'s <c>Completed</c> arm alone.
    /// Ordered newest-first like the list it recaps, so the fold is over a deterministic sequence.
    /// </summary>
    public async Task<IReadOnlyList<CompletedSessionFact>> ListCompletedHistoryFactsAsync(
        UserId viewerId,
        CancellationToken cancellationToken)
    {
        short completed = (short)SessionStatus.Completed;
        short going = (short)ParticipationStatus.Going;
        Guid viewer = viewerId.Value;
        string predicate = SessionVisibilitySql.Predicate(SessionVisibilitySql.AccessKind.Any);

        List<Guid> ids = await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
            SELECT id FROM sessions s
            WHERE status = {2}
              AND {{TookPartSql}}
              AND {{predicate}}
            ORDER BY start_time DESC, id DESC
            """, viewer, going, completed))
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return [];
        }

        List<SessionId> sessionIds = [.. ids.Select(SessionId.From)];
        List<Session> sessions = await context.Sessions
            .AsNoTracking()
            .Include(session => session.Participations)
            .Where(session => sessionIds.Contains(session.Id))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, Session> byId = sessions.ToDictionary(session => session.Id.Value);
        return [.. ids.Select(id => ToFact(byId[id], viewerId))];
    }

    public async Task<IReadOnlyDictionary<Guid, UserNameplate>> FindNameplatesAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, UserNameplate>();
        }

        List<UserId> ids = [.. userIds.Distinct()];
        var rows = await context.DomainUsers
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.DisplayName, user.Avatar })
            .ToListAsync(cancellationToken);

        Dictionary<Guid, UserNameplate> result = [];
        foreach (var row in rows)
        {
            result[row.Id.Value] = new UserNameplate(row.DisplayName, row.Avatar);
        }

        return result;
    }

    private static CompletedSessionFact ToFact(Session session, UserId viewerId)
    {
        List<UserId> companions = [.. PlayerIdsInOrder(session)
            .Where(userId => userId != viewerId)];

        return new CompletedSessionFact(
            session.Id,
            session.Duration.TotalMinutes,
            session.Game?.Name,
            companions);
    }

    /// <summary>
    /// D-221: the organizer first, then every other active <c>Going</c> registered participant in
    /// (joined-at, participation id) order — a total order that does not depend on how EF happened to
    /// materialize the collection. A waitlisted <c>Going</c> row never occupied a slot (D-084), so it
    /// is not a player; a guest has no user identity and cannot be one either.
    /// </summary>
    private static IEnumerable<UserId> PlayerIdsInOrder(Session session)
    {
        HashSet<UserId> seen = [];
        foreach (Participation participation in session.Participations
            .Where(p => p.IsActiveGoing && p.UserId is not null)
            .OrderByDescending(p => p.UserId == session.OrganizerId)
            .ThenBy(p => p.CreatedAt)
            .ThenBy(p => p.Id.Value))
        {
            UserId userId = participation.UserId!.Value;
            if (seen.Add(userId))
            {
                yield return userId;
            }
        }
    }

    private async Task<List<SessionSummary>> AttachPlayersAsync(
        List<Session> sessions,
        List<SessionSummary> summaries,
        CancellationToken cancellationToken)
    {
        if (sessions.Count == 0)
        {
            return summaries;
        }

        Dictionary<Guid, List<UserId>> playersBySession = sessions.ToDictionary(
            session => session.Id.Value,
            session => PlayerIdsInOrder(session).ToList());

        IReadOnlyDictionary<Guid, UserNameplate> nameplates = await FindNameplatesAsync(
            [.. playersBySession.Values.SelectMany(ids => ids)], cancellationToken);

        return [.. summaries.Select(summary => summary with
        {
            Players = [.. playersBySession[summary.Id.Value].Select(userId =>
            {
                UserNameplate? nameplate = nameplates.GetValueOrDefault(userId.Value);
                return new HistoryPlayer(userId, nameplate?.DisplayName, nameplate?.Avatar);
            })],
        })];
    }

    /// <summary>
    /// PHASE5_DESIGN.md A-077 (c)/(f): <c>inheritedOnly</c> plus the already-attached exclusion set —
    /// organizer, any participation row whatever its status (<c>Removed</c> included, A-077 (d)), or a
    /// Pending join request. Rule 4's block filter is inside the <c>inheritedOnly</c> fragment itself.
    /// </summary>
    public async Task<SessionPage<SessionSummary>> ListDiscoverAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        short completed = (short)SessionStatus.Completed;
        short expired = (short)SessionStatus.Expired;
        short canceled = (short)SessionStatus.Canceled;
        short pendingJoinRequest = (short)JoinRequestStatus.Pending;
        Guid viewer = viewerId.Value;
        string predicate = SessionVisibilitySql.Predicate(SessionVisibilitySql.AccessKind.InheritedOnly);

        List<Guid> ids = cursor is { } after
            ? await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM sessions s
                WHERE status NOT IN ({1}, {2}, {3})
                  AND s.organizer_id <> {0}
                  AND NOT EXISTS (SELECT 1 FROM session_participations sp WHERE sp.session_id = s.id AND sp.user_id = {0})
                  AND NOT EXISTS (SELECT 1 FROM join_requests jr WHERE jr.session_id = s.id AND jr.requester_user_id = {0} AND jr.status = {4})
                  AND (start_time, id) > ({5}, {6})
                  AND {{predicate}}
                ORDER BY start_time, id
                LIMIT {7}
                """, viewer, canceled, expired, completed, pendingJoinRequest, after.SortKey, after.Id, pageSize))
                .ToListAsync(cancellationToken)
            : await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM sessions s
                WHERE status NOT IN ({1}, {2}, {3})
                  AND s.organizer_id <> {0}
                  AND NOT EXISTS (SELECT 1 FROM session_participations sp WHERE sp.session_id = s.id AND sp.user_id = {0})
                  AND NOT EXISTS (SELECT 1 FROM join_requests jr WHERE jr.session_id = s.id AND jr.requester_user_id = {0} AND jr.status = {4})
                  AND {{predicate}}
                ORDER BY start_time, id
                LIMIT {5}
                """, viewer, canceled, expired, completed, pendingJoinRequest, pageSize))
                .ToListAsync(cancellationToken);

        List<Session> sessions = await HydrateSessionsInOrderAsync(ids, cancellationToken);
        List<SessionSummary> summaries = await BuildSummariesAsync(sessions, viewerId, cancellationToken);

        // round-1 fix (F5): mints its own page's cursor at the named Discover scope directly — this
        // is Discover's only caller, so no endpoint-side rebind step is needed the way
        // Upcoming/History/JoinRequests require one (their one shared repository helper cannot know
        // which of several possible callers is asking).
        return ToSummaryPage(summaries, pageSize, SessionListScope.Discover);
    }

    /// <summary>
    /// PHASE5_DESIGN.md A-080: the calendar <c>sessions</c> layer. Access-kind <c>any</c>, so it
    /// already includes inherited-access sessions the viewer is not attached to (wider than Discover,
    /// A-077 (d)) — each such row's <see cref="SessionSummary.ViewerRelation"/> is
    /// <see cref="ViewerRelation.Inherited"/>. The intersection predicate is arithmetic over immutable
    /// inputs, never a status re-derivation; <c>Canceled</c>/<c>Expired</c> are excluded and
    /// <c>Completed</c> is included (D-056). Unpaginated (A-079) — ordered for determinism only.
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> ListCalendarSessionsAsync(
        UserId viewerId,
        TimeRange range,
        CancellationToken cancellationToken)
    {
        short expired = (short)SessionStatus.Expired;
        short canceled = (short)SessionStatus.Canceled;
        Guid viewer = viewerId.Value;
        string predicate = SessionVisibilitySql.Predicate(SessionVisibilitySql.AccessKind.Any);

        List<Guid> ids = await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
            SELECT id FROM sessions s
            WHERE status NOT IN ({1}, {2})
              AND start_time < {3}
              AND start_time + make_interval(mins => duration_minutes) > {4}
              AND {{predicate}}
            ORDER BY start_time, id
            """, viewer, canceled, expired, range.End, range.Start))
            .ToListAsync(cancellationToken);

        List<Session> sessions = await HydrateSessionsInOrderAsync(ids, cancellationToken);
        return await BuildSummariesAsync(sessions, viewerId, cancellationToken);
    }

    /// <summary>
    /// PHASE5_DESIGN.md §5.1/P5-HOME-01: D-103's Home region 1. Two independent, capped queries —
    /// invitations awaiting the viewer's response and join requests awaiting the viewer's organizer
    /// decision — both scoped to <paramref name="now"/>-future, Unconfirmed/Confirmed sessions.
    /// </summary>
    public async Task<ActionableInbox> ListActionableAsync(
        UserId viewerId,
        Instant now,
        int cap,
        CancellationToken cancellationToken)
    {
        short unconfirmed = (short)SessionStatus.Unconfirmed;
        short confirmed = (short)SessionStatus.Confirmed;
        short invited = (short)ParticipationOrigin.Invited;
        short pending = (short)ParticipationStatus.Pending;
        short pendingJoinRequest = (short)JoinRequestStatus.Pending;
        Guid viewer = viewerId.Value;
        string predicate = SessionVisibilitySql.Predicate(SessionVisibilitySql.AccessKind.Any);

        List<Guid> invitationSessionIds = await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
            SELECT s.id FROM sessions s
            JOIN session_participations sp
              ON sp.session_id = s.id AND sp.user_id = {0} AND sp.status = {1} AND sp.origin = {2}
            WHERE s.status IN ({3}, {4})
              AND s.start_time > {5}
              AND {{predicate}}
            ORDER BY s.start_time, s.id
            LIMIT {6}
            """, viewer, pending, invited, unconfirmed, confirmed, now, cap + 1))
            .ToListAsync(cancellationToken);

        bool hasMoreInvitations = invitationSessionIds.Count > cap;
        if (hasMoreInvitations)
        {
            invitationSessionIds.RemoveAt(invitationSessionIds.Count - 1);
        }

        List<Session> invitationSessions = await HydrateSessionsInOrderAsync(invitationSessionIds, cancellationToken);
        List<SessionSummary> invitationSummaries = await BuildSummariesAsync(invitationSessions, viewerId, cancellationToken);
        List<ActionableInvitation> invitations = [];
        foreach (SessionSummary summary in invitationSummaries)
        {
            Session session = invitationSessions.Single(s => s.Id == summary.Id);
            Participation participation = session.Participations.First(p => p.UserId == viewerId);
            invitations.Add(new ActionableInvitation(summary, participation.CreatedAt));
        }

        List<Guid> joinRequestIds = await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
            SELECT jr.id FROM join_requests jr
            JOIN sessions s ON s.id = jr.session_id
            WHERE s.organizer_id = {0}
              AND jr.status = {1}
              AND s.status IN ({2}, {3})
              AND s.start_time > {4}
            ORDER BY s.start_time, s.id, jr.created_at
            LIMIT {5}
            """, viewer, pendingJoinRequest, unconfirmed, confirmed, now, cap + 1))
            .ToListAsync(cancellationToken);

        bool hasMoreJoinRequests = joinRequestIds.Count > cap;
        if (hasMoreJoinRequests)
        {
            joinRequestIds.RemoveAt(joinRequestIds.Count - 1);
        }

        List<JoinRequest> requests = await HydrateJoinRequestsInOrderAsync(joinRequestIds, cancellationToken);
        List<SessionId> requestSessionIds = [.. requests.Select(r => r.SessionId!.Value)];
        List<Session> requestSessions = requestSessionIds.Count == 0
            ? []
            : await context.Sessions
                .AsNoTracking()
                .Include(session => session.Participations)
                .Include(session => session.JoinRequests)
                .Where(session => requestSessionIds.Contains(session.Id))
                .ToListAsync(cancellationToken);
        List<SessionSummary> requestSessionSummaries = await BuildSummariesAsync(requestSessions, viewerId, cancellationToken);
        Dictionary<Guid, SessionSummary> summariesBySessionId = requestSessionSummaries.ToDictionary(s => s.Id.Value);
        Dictionary<Guid, DisplayName?> requesterNames = await FetchDisplayNamesAsync(
            [.. requests.Select(r => r.RequesterId)], cancellationToken);

        List<ActionableJoinRequest> joinRequests = [];
        foreach (JoinRequest request in requests)
        {
            SessionSummary summary = summariesBySessionId[request.SessionId!.Value.Value];
            DisplayName? requesterName = requesterNames.GetValueOrDefault(request.RequesterId.Value);
            joinRequests.Add(new ActionableJoinRequest(request.Id, summary, request.RequesterId, requesterName, request.CreatedAt));
        }

        return new ActionableInbox(invitations, joinRequests, hasMoreInvitations, hasMoreJoinRequests);
    }

    public async Task<SessionPage<JoinRequestSummary>> ListJoinRequestsAsync(
        SessionId sessionId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        List<Guid> ids = cursor is { } after
            ? await context.Database.SqlQuery<Guid>($"""
                SELECT id FROM join_requests
                WHERE session_id = {sessionId.Value}
                  AND (created_at, id) > ({after.SortKey}, {after.Id})
                ORDER BY created_at, id
                LIMIT {pageSize}
                """).ToListAsync(cancellationToken)
            : await context.Database.SqlQuery<Guid>($"""
                SELECT id FROM join_requests
                WHERE session_id = {sessionId.Value}
                ORDER BY created_at, id
                LIMIT {pageSize}
                """).ToListAsync(cancellationToken);

        List<JoinRequest> requests = await HydrateJoinRequestsInOrderAsync(ids, cancellationToken);
        Dictionary<Guid, DisplayName?> requesterNames = await FetchDisplayNamesAsync(
            [.. requests.Select(r => r.RequesterId)], cancellationToken);

        return ToPage(
            requests,
            pageSize,
            request => new SessionListCursor(request.CreatedAt, request.Id.Value),
            request => new JoinRequestSummary(
                request.Id,
                request.RequesterId,
                request.Status,
                request.CreatedAt,
                requesterNames.GetValueOrDefault(request.RequesterId.Value)));
    }

    /// <summary>
    /// A-072 keyset paging tie-breaks on <c>id</c>, which is a value-converted typed id: neither
    /// <c>Guid.CompareTo</c> nor <c>OrderBy(session.Id.Value)</c> translates to SQL. The page's
    /// ordered id list is therefore produced by a raw, Postgres-ordered row-value comparison
    /// (<c>(sort_key, id) &gt; (@k, @i)</c>) and the full rows are hydrated afterwards through the
    /// normal, translatable <c>Contains</c> shape, then re-sorted in memory to the id order the
    /// database already decided. <c>JoinRequests</c> is included so §5.1's <c>ViewerRelation</c> can
    /// resolve its <c>Requester</c> branch without a second query.
    /// </summary>
    private async Task<List<Session>> HydrateSessionsInOrderAsync(List<Guid> orderedIds, CancellationToken cancellationToken)
    {
        if (orderedIds.Count == 0)
        {
            return [];
        }

        List<SessionId> ids = [.. orderedIds.Select(SessionId.From)];
        List<Session> sessions = await context.Sessions
            .AsNoTracking()
            .Include(session => session.Participations)
            .Include(session => session.JoinRequests)
            .Where(session => ids.Contains(session.Id))
            .ToListAsync(cancellationToken);
        AudienceGroupOrdering.Normalize(sessions);

        Dictionary<Guid, Session> byId = sessions.ToDictionary(session => session.Id.Value);
        return [.. orderedIds.Select(id => byId[id])];
    }

    private async Task<List<JoinRequest>> HydrateJoinRequestsInOrderAsync(List<Guid> orderedIds, CancellationToken cancellationToken)
    {
        if (orderedIds.Count == 0)
        {
            return [];
        }

        List<JoinRequestId> ids = [.. orderedIds.Select(JoinRequestId.From)];
        List<JoinRequest> requests = await context.JoinRequests
            .AsNoTracking()
            .Where(request => ids.Contains(request.Id))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, JoinRequest> byId = requests.ToDictionary(request => request.Id.Value);
        return [.. orderedIds.Select(id => byId[id])];
    }

    public async Task<GuestSessionView?> FindGuestViewAsync(GuestLinkId guestLinkId, CancellationToken cancellationToken)
    {
        GuestLink? link = await context.GuestLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(guestLink => guestLink.Id == guestLinkId, cancellationToken);

        if (link?.SessionId is not { } sessionId)
        {
            return null;
        }

        Session? session = await context.Sessions
            .AsNoTracking()
            .Include(s => s.Participations)
            .Include(s => s.GuestIdentities)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        GuestIdentity? identity = link.GuestIdentityId is { } identityId
            ? session.GuestIdentities.FirstOrDefault(g => g.Id == identityId)
            : null;

        Participation? participation = identity is not null
            ? session.Find(identity.Participant)
            : null;

        return new GuestSessionView(
            session.Id,
            session.Title,
            session.StartTime,
            session.Duration,
            session.Platform,
            session.Game,
            session.Notes,
            identity?.Id,
            identity?.DisplayName,
            participation?.Status);
    }

    private async Task<SessionDetail> ToDetailAsync(Session session, uint xmin, CancellationToken cancellationToken)
    {
        Dictionary<Guid, UserNameplate> participantUsers = [];
        List<UserId> registeredIds = [.. session.Participations
            .Where(p => p.UserId.HasValue)
            .Select(p => p.UserId!.Value)];

        if (registeredIds.Count > 0)
        {
            var users = await context.DomainUsers
                .AsNoTracking()
                .Where(u => registeredIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName, u.Avatar })
                .ToListAsync(cancellationToken);

            foreach (var user in users)
            {
                participantUsers[user.Id.Value] = new UserNameplate(user.DisplayName, user.Avatar);
            }
        }

        List<RosterEntry> roster = [];
        foreach (Participation participation in session.Participations)
        {
            UserNameplate? participantUser = participation.UserId is { } userId
                ? participantUsers.GetValueOrDefault(userId.Value)
                : null;

            DisplayName? displayName = participation.UserId is not null
                ? participantUser?.DisplayName
                : participation.GuestIdentityId is { } guestId
                    ? session.GuestIdentities.FirstOrDefault(g => g.Id == guestId)?.DisplayName
                    : null;

            roster.Add(new RosterEntry(
                participation.Id,
                participation.Participant,
                displayName,
                participation.Status,
                participation.IsWaitlisted,
                participation.Origin,
                participation.ReminderChoice,
                Avatar: participantUser?.Avatar));
        }

        return new SessionDetail(
            session.Id,
            session.OrganizerId,
            session.Title,
            session.StartTime,
            session.Duration,
            session.Platform,
            session.Game,
            session.Notes,
            session.MinAttendance,
            session.MaxCapacity,
            session.Status,
            session.CreatedAt,
            roster,
            xmin,
            Visibility: session.Visibility);
    }

    /// <summary>
    /// PHASE5_DESIGN.md §5.1: the shared summary builder every summary-producing surface routes
    /// through — <c>ViewerRelation</c> and the organizer's display name (null for a tombstoned
    /// organizer, D-092) are resolved identically everywhere a <see cref="SessionSummary"/> is built.
    /// Returns summaries in the same order as <paramref name="sessions"/>.
    /// </summary>
    private async Task<List<SessionSummary>> BuildSummariesAsync(
        IReadOnlyList<Session> sessions, UserId viewerId, CancellationToken cancellationToken)
    {
        Dictionary<Guid, DisplayName?> organizerNames = await FetchDisplayNamesAsync(
            [.. sessions.Select(s => s.OrganizerId)], cancellationToken);

        // D-206: one batched query for the whole page, over the union of Groups-level audiences only.
        List<(Guid TargetId, IReadOnlyCollection<FriendGroupId> Audience)> audiences =
            [.. sessions
                .Where(session => session.Visibility == ContentVisibility.Groups)
                .Select(session => (session.Id.Value, session.AudienceGroupIds))];
        IReadOnlyDictionary<Guid, GroupProvenance> provenance = audiences.Count == 0
            ? new Dictionary<Guid, GroupProvenance>()
            : await GroupProvenanceReader.ResolveAsync(context, audiences, viewerId, cancellationToken);

        List<SessionSummary> summaries = [];
        foreach (Session session in sessions)
        {
            summaries.Add(new SessionSummary(
                session.Id,
                session.OrganizerId,
                session.Title,
                session.StartTime,
                session.Duration,
                session.Platform,
                session.Game,
                session.Status,
                session.MaxCapacity,
                session.ActiveOccupancy,
                organizerNames.GetValueOrDefault(session.OrganizerId.Value),
                ResolveViewerRelation(session, viewerId),
                session.Visibility,
                provenance.GetValueOrDefault(session.Id.Value)));
        }

        return summaries;
    }

    private async Task<Dictionary<Guid, DisplayName?>> FetchDisplayNamesAsync(
        IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        List<UserId> ids = [.. userIds.Distinct()];
        var rows = await context.DomainUsers
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToListAsync(cancellationToken);

        Dictionary<Guid, DisplayName?> result = [];
        foreach (var row in rows)
        {
            result[row.Id.Value] = row.DisplayName;
        }

        return result;
    }

    /// <summary>PHASE5_DESIGN.md §5.1's precedence table, over the shipped participation/join-request rows.</summary>
    private static ViewerRelation ResolveViewerRelation(Session session, UserId viewerId)
    {
        if (session.OrganizerId == viewerId)
        {
            return ViewerRelation.Organizer;
        }

        Participation? participation = session.Participations.FirstOrDefault(p => p.UserId == viewerId);
        if (participation is not null)
        {
            if (participation.Status == ParticipationStatus.Pending && participation.Origin == ParticipationOrigin.Invited)
            {
                return ViewerRelation.Invitee;
            }

            bool isParticipant = participation.Status
                    is ParticipationStatus.Going or ParticipationStatus.Maybe or ParticipationStatus.Declined
                || (participation.Status == ParticipationStatus.Pending && participation.Origin != ParticipationOrigin.Invited);
            if (isParticipant)
            {
                return ViewerRelation.Participant;
            }
        }

        bool hasPendingRequest = session.JoinRequests.Any(
            request => request.RequesterId == viewerId && request.Status == JoinRequestStatus.Pending);
        if (hasPendingRequest)
        {
            return ViewerRelation.Requester;
        }

        return ViewerRelation.Inherited;
    }

    private static SessionPage<SessionSummary> ToSummaryPage(
        List<SessionSummary> summaries, int pageSize, SessionListScope scope = SessionListScope.Unbound)
    {
        string? nextCursor = summaries.Count == pageSize && summaries.Count > 0
            ? new SessionListCursor(summaries[^1].StartTime, summaries[^1].Id.Value, scope).Encode()
            : null;

        return new SessionPage<SessionSummary>(summaries, nextCursor);
    }

    private static SessionPage<TOut> ToPage<TIn, TOut>(
        List<TIn> items,
        int pageSize,
        Func<TIn, SessionListCursor> cursorSelector,
        Func<TIn, TOut> selector)
    {
        string? nextCursor = items.Count == pageSize && items.Count > 0
            ? cursorSelector(items[^1]).Encode()
            : null;

        return new SessionPage<TOut>([.. items.Select(selector)], nextCursor);
    }
}
