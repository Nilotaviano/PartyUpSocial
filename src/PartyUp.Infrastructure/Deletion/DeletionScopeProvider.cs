using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Deletion;

/// <summary>
/// The real §6.2 pre-lock/reload protocol implementation. <see cref="DiscoverAffectedUserIdsAsync"/>
/// runs with no locks held, so <c>ExecuteDeletionForUserUseCase</c> can lock the complete discovered
/// user set in one A-068-ordered statement before calling <see cref="LoadForUpdateAsync"/>, which
/// reloads friendships, blocks, and affected session parents (with their children) under the normal
/// graph lock order. <see cref="LoadAsync"/> is preserved unchanged for callers that have not adopted
/// the widened protocol (Phase 3's friend-graph race tests among them).
/// </summary>
public sealed class DeletionScopeProvider(
    AppDbContext context,
    ISessionLockRepository sessionLocks,
    IUsernameRegistryRepository usernames)
    : IDeletionScopeProvider
{
    public async Task<DeletionScope> LoadAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM friendships
            WHERE user_lo_id = {id.Value} OR user_hi_id = {id.Value}
            ORDER BY user_lo_id, user_hi_id, id
            FOR UPDATE
            """, cancellationToken);

        List<Friendship> friendships = await context.Friendships
            .Where(friendship => friendship.UserLoId == id || friendship.UserHiId == id)
            .OrderBy(friendship => friendship.UserLoId)
            .ThenBy(friendship => friendship.UserHiId)
            .ThenBy(friendship => friendship.Id)
            .ToListAsync(cancellationToken);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM blocks
            WHERE blocker_id = {id.Value} OR blocked_id = {id.Value}
            ORDER BY LEAST(blocker_id, blocked_id),
                     GREATEST(blocker_id, blocked_id),
                     blocker_id,
                     blocked_id,
                     id
            FOR UPDATE
            """, cancellationToken);

        List<Block> loadedBlocks = await context.Blocks
            .Where(block => block.BlockerId == id || block.BlockedId == id)
            .ToListAsync(cancellationToken);
        Block[] blocks = loadedBlocks
            .OrderBy(block => Lower(block.BlockerId, block.BlockedId).Value)
            .ThenBy(block => Upper(block.BlockerId, block.BlockedId).Value)
            .ThenBy(block => block.BlockerId.Value)
            .ThenBy(block => block.BlockedId.Value)
            .ThenBy(block => block.Id.Value)
            .ToArray();

        UsernameRegistryEntry? username = await usernames.FindActiveForOwnerAsync(
            id,
            cancellationToken);
        return new DeletionScope([], [], friendships, blocks, username);
    }

    public async Task<IReadOnlyCollection<UserId>> DiscoverAffectedUserIdsAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        HashSet<UserId> ids = [id];

        List<UserId> friendPartners = await context.Friendships
            .Where(friendship => friendship.UserLoId == id || friendship.UserHiId == id)
            .Select(friendship => friendship.UserLoId == id ? friendship.UserHiId : friendship.UserLoId)
            .ToListAsync(cancellationToken);
        ids.UnionWith(friendPartners);

        List<UserId> blockPartners = await context.Blocks
            .Where(block => block.BlockerId == id || block.BlockedId == id)
            .Select(block => block.BlockerId == id ? block.BlockedId : block.BlockerId)
            .ToListAsync(cancellationToken);
        ids.UnionWith(blockPartners);

        List<SessionId> ownedSessionIds = await context.Sessions
            .Where(session => session.OrganizerId == id)
            .Select(session => session.Id)
            .ToListAsync(cancellationToken);

        if (ownedSessionIds.Count > 0)
        {
            List<UserId> ownedParticipants = await context.SessionParticipations
                .Where(participation =>
                    ownedSessionIds.Contains(participation.SessionId) && participation.UserId != null)
                .Select(participation => participation.UserId!.Value)
                .ToListAsync(cancellationToken);
            ids.UnionWith(ownedParticipants);

            List<UserId> ownedRequesters = await context.JoinRequests
                .Where(request =>
                    request.SessionId != null &&
                    ownedSessionIds.Contains(request.SessionId.Value) &&
                    request.Status == JoinRequestStatus.Pending)
                .Select(request => request.RequesterId)
                .ToListAsync(cancellationToken);
            ids.UnionWith(ownedRequesters);
        }

        List<UserId> participatedOrganizers = await context.SessionParticipations
            .Where(participation => participation.UserId == id)
            .Join(
                context.Sessions,
                participation => participation.SessionId,
                session => session.Id,
                (participation, session) => session.OrganizerId)
            .Where(organizerId => organizerId != id)
            .ToListAsync(cancellationToken);
        ids.UnionWith(participatedOrganizers);

        List<UserId> requestedOrganizers = await context.JoinRequests
            .Where(request =>
                request.RequesterId == id &&
                request.SessionId != null &&
                request.Status == JoinRequestStatus.Pending)
            .Join(
                context.Sessions,
                request => request.SessionId!.Value,
                session => session.Id,
                (request, session) => session.OrganizerId)
            .Where(organizerId => organizerId != id)
            .ToListAsync(cancellationToken);
        ids.UnionWith(requestedOrganizers);

        return ids;
    }

    public async Task<DeletionScope> LoadForUpdateAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        DeletionScope baseline = await LoadAsync(id, cancellationToken);

        List<SessionId> sessionIds = await context.Sessions
            .Where(session =>
                session.OrganizerId == id ||
                session.Participations.Any(participation => participation.UserId == id) ||
                session.JoinRequests.Any(request =>
                    request.RequesterId == id && request.Status == JoinRequestStatus.Pending))
            .Select(session => session.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (sessionIds.Count == 0)
        {
            return baseline with { Sessions = [] };
        }

        await sessionLocks.LockSessionsAsync(sessionIds, cancellationToken);

        List<Session> sessions = await LoadWithChildren(context.Sessions)
            .Where(session => sessionIds.Contains(session.Id))
            .ToListAsync(cancellationToken);

        return baseline with { Sessions = sessions };
    }

    private static IQueryable<Session> LoadWithChildren(IQueryable<Session> query) =>
        query
            .Include(session => session.Participations)
            .Include(session => session.JoinRequests)
            .Include(session => session.GuestIdentities)
            .Include(session => session.GuestLinks);

    private static UserId Lower(UserId first, UserId second) =>
        first.Value.CompareTo(second.Value) < 0 ? first : second;

    private static UserId Upper(UserId first, UserId second) =>
        first.Value.CompareTo(second.Value) > 0 ? first : second;
}
