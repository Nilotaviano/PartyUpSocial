using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Friends;

/// <summary>
/// The real session and poll halves of <see cref="IBlockEffectScopeProvider"/> (PHASE4_DESIGN.md §5.3,
/// PHASE6_DESIGN.md §2.1), replacing the Phase 3 <c>EmptyBlockEffectScopeProvider</c> on the same
/// shipped port. Discovers every session/poll where one party organizes and the other holds a
/// participation or a pending join request, locks the affected session parents and then the affected
/// poll parents — in that order, each in its own A-068/P3-LK-01-ordered statement — so a concurrent
/// pair of blocks sharing parents cannot take them in opposite orders, then loads them with the
/// children <see cref="Domain.Friendships.BlockEffects.Apply"/> needs to reapply eligibility under
/// lock. The domain decides the actual gate (terminal/past-window, §116); this provider only feeds
/// candidates.
/// </summary>
public sealed class BlockEffectScopeProvider(
    AppDbContext context,
    ISessionLockRepository sessionLocks,
    IPollLockRepository pollLocks)
    : IBlockEffectScopeProvider
{
    public async Task<BlockEffectScope> LoadForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        List<SessionId> sessionCandidateIds = await context.Sessions
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

        List<Session> sessions = [];
        if (sessionCandidateIds.Count > 0)
        {
            await sessionLocks.LockSessionsAsync(sessionCandidateIds, cancellationToken);

            sessions = await LoadSessionWithChildren(context.Sessions)
                .Where(session => sessionCandidateIds.Contains(session.Id))
                .ToListAsync(cancellationToken);
        }

        // Poll parents lock strictly after the session parents above (P3-LK-01), so two blocks that
        // share both a session and a poll always acquire the two parent kinds in the same order.
        List<PollId> pollCandidateIds = await context.Polls
            .Where(poll =>
                (poll.OrganizerId == first &&
                    (poll.Participants.Any(participant =>
                            participant.UserId == second && participant.Status != PollParticipantStatus.Removed) ||
                     poll.JoinRequests.Any(request =>
                         request.RequesterId == second && request.Status == JoinRequestStatus.Pending))) ||
                (poll.OrganizerId == second &&
                    (poll.Participants.Any(participant =>
                            participant.UserId == first && participant.Status != PollParticipantStatus.Removed) ||
                     poll.JoinRequests.Any(request =>
                         request.RequesterId == first && request.Status == JoinRequestStatus.Pending))))
            .Select(poll => poll.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        List<Poll> polls = [];
        if (pollCandidateIds.Count > 0)
        {
            await pollLocks.LockPollsAsync(pollCandidateIds, cancellationToken);

            polls = await LoadPollWithChildren(context.Polls)
                .Where(poll => pollCandidateIds.Contains(poll.Id))
                .ToListAsync(cancellationToken);
        }

        return new BlockEffectScope(sessions, polls);
    }

    private static IQueryable<Session> LoadSessionWithChildren(IQueryable<Session> query) =>
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
}
