using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
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
/// <see cref="LoadAvailabilityForUpdateAsync"/> is the Phase 5 (A-075) tail: it runs after every lock
/// above and is called directly by <see cref="PartyUp.Infrastructure.Friends.DeletionResultApplier"/>,
/// which is where §11 step 5's availability deletion actually happens (A-075's lock-order tail is
/// appended to an already-open transaction, never a new one).
/// </summary>
public sealed class DeletionScopeProvider(
    AppDbContext context,
    ISessionLockRepository sessionLocks,
    IPollLockRepository pollLocks,
    IUsernameRegistryRepository usernames,
    IAvailabilityRepository availability)
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

        List<PollId> ownedPollIds = await context.Polls
            .Where(poll => poll.OrganizerId == id)
            .Select(poll => poll.Id)
            .ToListAsync(cancellationToken);

        if (ownedPollIds.Count > 0)
        {
            List<UserId> ownedPollParticipants = await context.PollParticipants
                .Where(participant =>
                    ownedPollIds.Contains(participant.PollId) && participant.UserId != null)
                .Select(participant => participant.UserId!.Value)
                .ToListAsync(cancellationToken);
            ids.UnionWith(ownedPollParticipants);

            List<UserId> ownedPollRequesters = await context.JoinRequests
                .Where(request =>
                    request.PollId != null &&
                    ownedPollIds.Contains(request.PollId.Value) &&
                    request.Status == JoinRequestStatus.Pending)
                .Select(request => request.RequesterId)
                .ToListAsync(cancellationToken);
            ids.UnionWith(ownedPollRequesters);
        }

        List<UserId> participatedPollOrganizers = await context.PollParticipants
            .Where(participant => participant.UserId == id)
            .Join(
                context.Polls,
                participant => participant.PollId,
                poll => poll.Id,
                (participant, poll) => poll.OrganizerId)
            .Where(organizerId => organizerId != id)
            .ToListAsync(cancellationToken);
        ids.UnionWith(participatedPollOrganizers);

        List<UserId> requestedPollOrganizers = await context.JoinRequests
            .Where(request =>
                request.RequesterId == id &&
                request.PollId != null &&
                request.Status == JoinRequestStatus.Pending)
            .Join(
                context.Polls,
                request => request.PollId!.Value,
                poll => poll.Id,
                (request, poll) => poll.OrganizerId)
            .Where(organizerId => organizerId != id)
            .ToListAsync(cancellationToken);
        ids.UnionWith(requestedPollOrganizers);

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

        List<Session> sessions = [];
        if (sessionIds.Count > 0)
        {
            await sessionLocks.LockSessionsAsync(sessionIds, cancellationToken);

            sessions = await LoadWithChildren(context.Sessions)
                .Where(session => sessionIds.Contains(session.Id))
                .ToListAsync(cancellationToken);
        }

        // Poll parents lock strictly after every session parent above (P3-LK-01), so a subject with
        // both session and poll scope always acquires the two parent kinds in the same order.
        List<PollId> pollIds = await context.Polls
            .Where(poll =>
                poll.OrganizerId == id ||
                poll.Participants.Any(participant => participant.UserId == id) ||
                poll.JoinRequests.Any(request =>
                    request.RequesterId == id && request.Status == JoinRequestStatus.Pending))
            .Select(poll => poll.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        List<Poll> polls = [];
        if (pollIds.Count > 0)
        {
            await pollLocks.LockPollsAsync(pollIds, cancellationToken);

            polls = await LoadPollWithChildren(context.Polls)
                .Where(poll => pollIds.Contains(poll.Id))
                .ToListAsync(cancellationToken);
        }

        return baseline with { Sessions = sessions, Polls = polls };
    }

    /// <summary>
    /// A-075's availability tail: series (owner-derived), blocks (owner-derived — already every
    /// materialized block too, §5.1), then those series' skips — one statement per table, in the
    /// declared order, over rows the caller is about to delete.
    /// </summary>
    public async Task<AvailabilityDeletionScope> LoadAvailabilityForUpdateAsync(
        UserId ownerId, CancellationToken cancellationToken)
    {
        IReadOnlyList<AvailabilitySeries> series = await availability.LockOwnerSeriesAsync(ownerId, cancellationToken);
        IReadOnlyList<AvailabilityBlock> blocks = await availability.LockBlocksByOwnerAsync(ownerId, cancellationToken);

        List<AvailabilitySeriesId> seriesIds = [.. series.Select(s => s.Id)];
        IReadOnlyList<AvailabilityOccurrenceSkip> skips = seriesIds.Count == 0
            ? []
            : await availability.LockSkipsAsync(seriesIds, cancellationToken);

        return new AvailabilityDeletionScope(series, blocks, skips);
    }

    private static IQueryable<Session> LoadWithChildren(IQueryable<Session> query) =>
        query
            .Include(session => session.Participations)
            .Include(session => session.JoinRequests)
            .Include(session => session.GuestIdentities)
            .Include(session => session.GuestLinks);

    private static IQueryable<Poll> LoadPollWithChildren(IQueryable<Poll> query) =>
        query
            .Include(poll => poll.Options)
            .Include(poll => poll.Participants)
            .Include(poll => poll.Votes)
            .Include(poll => poll.JoinRequests)
            .Include(poll => poll.GuestIdentities)
            .Include(poll => poll.GuestLinks);

    private static UserId Lower(UserId first, UserId second) =>
        first.Value.CompareTo(second.Value) < 0 ? first : second;

    private static UserId Upper(UserId first, UserId second) =>
        first.Value.CompareTo(second.Value) > 0 ? first : second;
}
