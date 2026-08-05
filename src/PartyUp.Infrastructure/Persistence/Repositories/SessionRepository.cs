using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The Session aggregate's own persistence adapter (PHASE4_DESIGN.md §4.2). <see cref="LoadForUpdateAsync"/>
/// follows the <see cref="Repositories.FriendPairLocker"/>/<c>UserRepository.FindForUpdateAsync</c>
/// idiom: a raw, database-ordered lock statement first, then an EF query for the locked row and its
/// children — never the other way around (A-068/A-069).
/// </summary>
public sealed class SessionRepository(AppDbContext context) : ISessionRepository
{
    public void Add(Session session) => context.Sessions.Add(session);

    public Task<Session?> FindAsync(SessionId id, CancellationToken cancellationToken) =>
        LoadWithChildren(context.Sessions).FirstOrDefaultAsync(session => session.Id == id, cancellationToken);

    public async Task<Session?> LoadForUpdateAsync(SessionId id, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM sessions
            WHERE id = {id.Value}
            FOR NO KEY UPDATE
            """, cancellationToken);

        return await LoadWithChildren(context.Sessions)
            .FirstOrDefaultAsync(session => session.Id == id, cancellationToken);
    }

    private static IQueryable<Session> LoadWithChildren(IQueryable<Session> query) =>
        query
            .Include(session => session.Participations)
            .Include(session => session.JoinRequests)
            .Include(session => session.GuestIdentities)
            .Include(session => session.GuestLinks);
}
