using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The Session aggregate's own persistence adapter (PHASE4_DESIGN.md §4.2). <see cref="LoadForUpdateAsync"/>
/// follows the <see cref="Repositories.FriendPairLocker"/>/<c>UserRepository.FindForUpdateAsync</c>
/// idiom: a raw, database-ordered lock statement first, then an EF query for the locked row and its
/// children — never the other way around (A-068/A-069). Mutes and handoffs are not aggregate children,
/// but §4.2 requires them read under the same parent lock, so they are queried alongside the aggregate
/// before the lock statement's transaction can be interleaved by anyone else.
/// </summary>
public sealed class SessionRepository(AppDbContext context) : ISessionRepository
{
    public void Add(Session session) => context.Sessions.Add(session);

    public async Task<Session?> FindAsync(SessionId id, CancellationToken cancellationToken)
    {
        Session? session = await LoadWithChildren(context.Sessions)
            .FirstOrDefaultAsync(session => session.Id == id, cancellationToken);
        AudienceGroupOrdering.Normalize(session);
        return session;
    }

    /// <summary>Untracked on purpose (A-069/§6.1 step 1) — see the interface doc for why.</summary>
    public async Task<Session?> FindNoTrackingAsync(SessionId id, CancellationToken cancellationToken)
    {
        Session? session = await LoadWithChildren(context.Sessions.AsNoTracking())
            .FirstOrDefaultAsync(session => session.Id == id, cancellationToken);
        AudienceGroupOrdering.Normalize(session);
        return session;
    }

    public async Task<SessionForUpdate?> LoadForUpdateAsync(SessionId id, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM sessions
            WHERE id = {id.Value}
            FOR NO KEY UPDATE
            """, cancellationToken);

        Session? session = await LoadWithChildren(context.Sessions)
            .FirstOrDefaultAsync(session => session.Id == id, cancellationToken);
        AudienceGroupOrdering.Normalize(session);

        if (session is null)
        {
            return null;
        }

        List<NotificationMute> mutes = await context.NotificationMutes
            .Where(mute => mute.SessionId == id)
            .ToListAsync(cancellationToken);

        List<SessionReminderHandoffLock> handoffs = await context.SessionReminderHandoffs
            .Where(handoff => handoff.SessionId == id)
            .Select(handoff => new SessionReminderHandoffLock(
                handoff.Id,
                handoff.RecipientUserId,
                handoff.DueAt,
                (int)handoff.State,
                handoff.Version,
                handoff.Attempts))
            .ToListAsync(cancellationToken);

        return new SessionForUpdate(session, mutes, handoffs);
    }

    public async Task<IReadOnlyList<FutureEligibleSession>> ListFutureSessionsWithEligibleParticipationAsync(
        UserId userId,
        Instant now,
        (Instant StartTime, Guid Id)? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        Guid user = userId.Value;
        short unconfirmed = (short)SessionStatus.Unconfirmed;
        short confirmed = (short)SessionStatus.Confirmed;
        short declined = (short)ParticipationStatus.Declined;
        short removed = (short)ParticipationStatus.Removed;

        List<FutureEligibleSessionRow> rows = afterCursor is { } after
            ? await context.Database.SqlQuery<FutureEligibleSessionRow>($"""
                SELECT s.id, s.start_time
                FROM sessions s
                JOIN session_participations sp ON sp.session_id = s.id AND sp.user_id = {user}
                WHERE s.status IN ({unconfirmed}, {confirmed})
                  AND sp.status NOT IN ({declined}, {removed})
                  AND s.start_time > {now}
                  AND (s.start_time, s.id) > ({after.StartTime}, {after.Id})
                ORDER BY s.start_time, s.id
                LIMIT {limit}
                """).ToListAsync(cancellationToken)
            : await context.Database.SqlQuery<FutureEligibleSessionRow>($"""
                SELECT s.id, s.start_time
                FROM sessions s
                JOIN session_participations sp ON sp.session_id = s.id AND sp.user_id = {user}
                WHERE s.status IN ({unconfirmed}, {confirmed})
                  AND sp.status NOT IN ({declined}, {removed})
                  AND s.start_time > {now}
                ORDER BY s.start_time, s.id
                LIMIT {limit}
                """).ToListAsync(cancellationToken);

        return [.. rows.Select(row => new FutureEligibleSession(SessionId.From(row.Id), row.StartTime))];
    }

    private sealed record FutureEligibleSessionRow(Guid Id, Instant StartTime);

    private static IQueryable<Session> LoadWithChildren(IQueryable<Session> query) =>
        query
            .Include(session => session.Participations)
            .Include(session => session.JoinRequests)
            .Include(session => session.GuestIdentities)
            .Include(session => session.GuestLinks);
}
