using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Friends;

/// <summary>
/// The real session half of <see cref="IBlockEffectScopeProvider"/> (PHASE4_DESIGN.md §5.3), replacing
/// the Phase 3 <c>EmptyBlockEffectScopeProvider</c> on the same shipped port. Discovers every session
/// where one party organizes and the other holds a participation or a pending join request, locks the
/// affected session parents in one A-068-ordered statement so a concurrent pair of blocks sharing two
/// sessions cannot take them in opposite orders, then loads them with the children
/// <see cref="Domain.Friendships.BlockEffects.Apply"/> needs to reapply eligibility under lock. Polls
/// are Phase 6 scope and are always empty here.
/// </summary>
public sealed class SessionBlockEffectScopeProvider(
    AppDbContext context,
    ISessionLockRepository sessionLocks)
    : IBlockEffectScopeProvider
{
    public async Task<BlockEffectScope> LoadForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        List<SessionId> candidateIds = await context.Sessions
            .Where(session =>
                (session.OrganizerId == first &&
                    (session.Participations.Any(participation => participation.UserId == second) ||
                     session.JoinRequests.Any(request =>
                         request.RequesterId == second && request.Status == JoinRequestStatus.Pending))) ||
                (session.OrganizerId == second &&
                    (session.Participations.Any(participation => participation.UserId == first) ||
                     session.JoinRequests.Any(request =>
                         request.RequesterId == first && request.Status == JoinRequestStatus.Pending))))
            .Select(session => session.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (candidateIds.Count == 0)
        {
            return BlockEffectScope.Empty;
        }

        await sessionLocks.LockSessionsAsync(candidateIds, cancellationToken);

        List<Session> sessions = await LoadWithChildren(context.Sessions)
            .Where(session => candidateIds.Contains(session.Id))
            .ToListAsync(cancellationToken);

        return new BlockEffectScope(sessions, []);
    }

    private static IQueryable<Session> LoadWithChildren(IQueryable<Session> query) =>
        query
            .Include(session => session.Participations)
            .Include(session => session.JoinRequests)
            .Include(session => session.GuestIdentities)
            .Include(session => session.GuestLinks);
}
