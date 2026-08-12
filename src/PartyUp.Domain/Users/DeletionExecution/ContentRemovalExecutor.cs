using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Users.DeletionExecution;

/// <summary>
/// What one content removal did: DOMAIN_MODEL §11 steps 1–3 and nothing else. It carries no
/// tombstone, no username reservation, no friendship/block instruction and no journal entry, because
/// the ban-path caller performs none of those — <see cref="DeletionExecutionResult"/> widens this
/// into the full deletion payload rather than the other way round.
/// </summary>
public sealed record ContentRemovalResult(
    IReadOnlyList<SessionId> CanceledSessions,
    IReadOnlyList<PollId> ExpiredPolls,
    IReadOnlyList<SessionId> RemovedFromSessions,
    IReadOnlyList<PollId> RemovedFromPolls,
    IReadOnlyList<JoinRequestId> LapsedJoinRequests,
    IReadOnlyList<SessionReminderCanceled> CanceledReminders);

/// <summary>
/// DOMAIN_MODEL §11 steps 1–3 over an already-loaded, already-locked <see cref="DeletionScope"/>:
/// cancel the subject's not-started owned sessions, expire their live owned polls, and remove them
/// from other people's not-started content (lapsing their own pending join requests and canceling the
/// reminders each removal orphans). Two callers share it — <see cref="DeletionExecutor"/>, which runs
/// it as the first three of its seven ordered steps, and the D-180 optional content-removal action,
/// which runs exactly these three and stops (PHASE8_DESIGN.md §3 S2). The subject is passed in rather
/// than read off a <see cref="User"/>: the removal path deliberately makes no lifecycle claim about
/// the account, which stays a normal live (banned) account.
/// </summary>
public static class ContentRemovalExecutor
{
    public static ContentRemovalResult Execute(UserId subject, DeletionScope scope, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();
        ParticipantRef self = ParticipantRef.ForUser(subject);

        List<SessionId> canceledSessions = [];
        List<PollId> expiredPolls = [];
        List<SessionId> removedFromSessions = [];
        List<PollId> removedFromPolls = [];
        List<JoinRequestId> lapsedJoinRequests = [];
        List<SessionReminderCanceled> canceledReminders = [];

        // 1. Cancel owned sessions that have not started. Owned in-progress sessions run to
        //    completion (§12-35 — canceling would yank live guests) and owned Completed sessions stay
        //    in history (D-092).
        foreach (Session session in scope.Sessions)
        {
            if (session.OrganizerId != subject || session.IsTerminal || now >= session.StartTime)
            {
                continue;
            }

            session.Cancel(clock);
            canceledSessions.Add(session.Id);
        }

        // 2. Expire owned Polling/Closed polls — the deletion-expiry transition, D-170 notifications,
        //    guest links to ExpiredPoll.
        foreach (Poll poll in scope.Polls)
        {
            if (poll.OrganizerId != subject || poll.IsTerminal)
            {
                continue;
            }

            poll.ExpireForOrganizerDeletion(clock);
            expiredPolls.Add(poll.Id);
        }

        // 3. Remove self from other people's content. "Future" is uniformly not-started (§12-35): an
        //    in-progress participation completes, and confirmation is re-evaluated by the session
        //    itself (D-074) with the pending reminder canceled (§9).
        foreach (Session session in scope.Sessions)
        {
            if (session.OrganizerId == subject)
            {
                continue;
            }

            CollectPending(session.JoinRequests, subject, lapsedJoinRequests);
            session.LapseJoinRequestsFrom(subject, clock);

            if (session.IsTerminal || now >= session.StartTime)
            {
                continue;
            }

            if (session.Find(self) is not { IsRemoved: false })
            {
                continue;
            }

            session.RemoveParticipation(self, clock);
            removedFromSessions.Add(session.Id);
            canceledReminders.Add(new SessionReminderCanceled(session.Id, subject));
        }

        foreach (Poll poll in scope.Polls)
        {
            if (poll.OrganizerId == subject)
            {
                continue;
            }

            CollectPending(poll.JoinRequests, subject, lapsedJoinRequests);
            poll.LapseJoinRequestsFrom(subject, clock);

            // A removed user must never be mapped into a session finalized later (D-092); a poll
            // that is logically past its selection window can no longer finalize anything.
            if (poll.IsTerminal || now >= poll.SelectionWindowEndsAt)
            {
                continue;
            }

            if (poll.FindParticipant(self) is not { IsRemoved: false })
            {
                continue;
            }

            poll.RemoveParticipant(self, clock);
            removedFromPolls.Add(poll.Id);
        }

        return new ContentRemovalResult(
            canceledSessions,
            expiredPolls,
            removedFromSessions,
            removedFromPolls,
            lapsedJoinRequests,
            canceledReminders);
    }

    private static void CollectPending(
        IReadOnlyList<JoinRequest> requests,
        UserId requesterId,
        List<JoinRequestId> into)
    {
        foreach (JoinRequest request in requests)
        {
            if (request.RequesterId == requesterId && request.IsPending)
            {
                into.Add(request.Id);
            }
        }
    }
}
