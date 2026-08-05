using Microsoft.EntityFrameworkCore;
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

    public Task<Session?> FindAsync(SessionId id, CancellationToken cancellationToken) =>
        LoadWithChildren(context.Sessions).FirstOrDefaultAsync(session => session.Id == id, cancellationToken);

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

    private static IQueryable<Session> LoadWithChildren(IQueryable<Session> query) =>
        query
            .Include(session => session.Participations)
            .Include(session => session.JoinRequests)
            .Include(session => session.GuestIdentities)
            .Include(session => session.GuestLinks);
}
