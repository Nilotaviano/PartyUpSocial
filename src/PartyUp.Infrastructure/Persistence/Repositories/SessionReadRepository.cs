using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The read-only Session projections (PHASE4_DESIGN.md §5.1). Every query is <c>AsNoTracking</c> and
/// applies no visibility or identity masking of its own — that is layered on by the caller.
/// </summary>
public sealed class SessionReadRepository(AppDbContext context) : ISessionReadRepository
{
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
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        short completed = (short)SessionStatus.Completed;
        short expired = (short)SessionStatus.Expired;
        short canceled = (short)SessionStatus.Canceled;

        List<Guid> ids = cursor is { } after
            ? await context.Database.SqlQuery<Guid>($"""
                SELECT id FROM sessions
                WHERE status NOT IN ({canceled}, {expired}, {completed})
                  AND (start_time, id) > ({after.SortKey}, {after.Id})
                ORDER BY start_time, id
                LIMIT {pageSize}
                """).ToListAsync(cancellationToken)
            : await context.Database.SqlQuery<Guid>($"""
                SELECT id FROM sessions
                WHERE status NOT IN ({canceled}, {expired}, {completed})
                ORDER BY start_time, id
                LIMIT {pageSize}
                """).ToListAsync(cancellationToken);

        List<Session> sessions = await HydrateSessionsInOrderAsync(ids, cancellationToken);

        return ToPage(sessions, pageSize, session => new SessionListCursor(session.StartTime, session.Id.Value));
    }

    public async Task<SessionPage<SessionSummary>> ListHistoryAsync(
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        short completed = (short)SessionStatus.Completed;

        List<Guid> ids = cursor is { } before
            ? await context.Database.SqlQuery<Guid>($"""
                SELECT id FROM sessions
                WHERE status = {completed}
                  AND (start_time, id) < ({before.SortKey}, {before.Id})
                ORDER BY start_time DESC, id DESC
                LIMIT {pageSize}
                """).ToListAsync(cancellationToken)
            : await context.Database.SqlQuery<Guid>($"""
                SELECT id FROM sessions
                WHERE status = {completed}
                ORDER BY start_time DESC, id DESC
                LIMIT {pageSize}
                """).ToListAsync(cancellationToken);

        List<Session> sessions = await HydrateSessionsInOrderAsync(ids, cancellationToken);

        return ToPage(sessions, pageSize, session => new SessionListCursor(session.StartTime, session.Id.Value));
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

        return ToPage(
            requests,
            pageSize,
            request => new SessionListCursor(request.CreatedAt, request.Id.Value),
            request => new JoinRequestSummary(request.Id, request.RequesterId, request.Status, request.CreatedAt));
    }

    /// <summary>
    /// A-072 keyset paging tie-breaks on <c>id</c>, which is a value-converted typed id: neither
    /// <c>Guid.CompareTo</c> nor <c>OrderBy(session.Id.Value)</c> translates to SQL. The page's
    /// ordered id list is therefore produced by a raw, Postgres-ordered row-value comparison
    /// (<c>(sort_key, id) &gt; (@k, @i)</c>) and the full rows are hydrated afterwards through the
    /// normal, translatable <c>Contains</c> shape, then re-sorted in memory to the id order the
    /// database already decided.
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
            .Where(session => ids.Contains(session.Id))
            .ToListAsync(cancellationToken);

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
        Dictionary<Guid, DisplayName?> userDisplayNames = [];
        List<UserId> registeredIds = [.. session.Participations
            .Where(p => p.UserId.HasValue)
            .Select(p => p.UserId!.Value)];

        if (registeredIds.Count > 0)
        {
            var users = await context.DomainUsers
                .AsNoTracking()
                .Where(u => registeredIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName })
                .ToListAsync(cancellationToken);

            foreach (var user in users)
            {
                userDisplayNames[user.Id.Value] = user.DisplayName;
            }
        }

        List<RosterEntry> roster = [];
        foreach (Participation participation in session.Participations)
        {
            DisplayName? displayName = participation.UserId is { } userId
                ? userDisplayNames.GetValueOrDefault(userId.Value)
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
                participation.ReminderChoice));
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
            session.IsPrivate,
            session.Status,
            session.CreatedAt,
            roster,
            xmin);
    }

    private static SessionPage<SessionSummary> ToPage(
        IReadOnlyList<Session> sessions,
        int pageSize,
        Func<Session, SessionListCursor> cursorSelector) =>
        ToPage(
            sessions,
            pageSize,
            cursorSelector,
            session => new SessionSummary(
                session.Id,
                session.OrganizerId,
                session.Title,
                session.StartTime,
                session.Duration,
                session.Platform,
                session.Game,
                session.Status,
                session.IsPrivate,
                session.MaxCapacity,
                session.ActiveOccupancy));

    private static SessionPage<TOut> ToPage<TIn, TOut>(
        IReadOnlyList<TIn> items,
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
