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
        IQueryable<Session> query = context.Sessions
            .AsNoTracking()
            .Where(session => session.Status != SessionStatus.Canceled
                && session.Status != SessionStatus.Expired
                && (session.Status != SessionStatus.Completed));

        if (cursor is { } after)
        {
            query = query.Where(session =>
                session.StartTime > after.SortKey
                || (session.StartTime == after.SortKey && session.Id.Value.CompareTo(after.Id) > 0));
        }

        List<Session> sessions = await query
            .OrderBy(session => session.StartTime)
            .ThenBy(session => session.Id.Value)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return ToPage(sessions, pageSize, session => new SessionListCursor(session.StartTime, session.Id.Value));
    }

    public async Task<SessionPage<SessionSummary>> ListHistoryAsync(
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        IQueryable<Session> query = context.Sessions
            .AsNoTracking()
            .Where(session => session.Status == SessionStatus.Completed);

        if (cursor is { } before)
        {
            query = query.Where(session =>
                session.StartTime < before.SortKey
                || (session.StartTime == before.SortKey && session.Id.Value.CompareTo(before.Id) < 0));
        }

        List<Session> sessions = await query
            .OrderByDescending(session => session.StartTime)
            .ThenByDescending(session => session.Id.Value)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return ToPage(sessions, pageSize, session => new SessionListCursor(session.StartTime, session.Id.Value));
    }

    public async Task<SessionPage<JoinRequestSummary>> ListJoinRequestsAsync(
        SessionId sessionId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        IQueryable<JoinRequest> query = context.JoinRequests
            .AsNoTracking()
            .Where(request => request.SessionId == sessionId);

        if (cursor is { } after)
        {
            query = query.Where(request =>
                request.CreatedAt > after.SortKey
                || (request.CreatedAt == after.SortKey && request.Id.Value.CompareTo(after.Id) > 0));
        }

        List<JoinRequest> requests = await query
            .OrderBy(request => request.CreatedAt)
            .ThenBy(request => request.Id.Value)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return ToPage(
            requests,
            pageSize,
            request => new SessionListCursor(request.CreatedAt, request.Id.Value),
            request => new JoinRequestSummary(request.Id, request.RequesterId, request.Status, request.CreatedAt));
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
