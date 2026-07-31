using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Users.DeletionExecution;

/// <summary>
/// The grace-expiry sweep of DOMAIN_MODEL §11: seven deliberately ordered steps over aggregates the
/// caller has already loaded and locked, in one transaction per user. Steps 1–3 run before step 4 so
/// their notifications still see the intact friend graph; the deleting user is never a recipient
/// (recipient computation excludes them).
/// </summary>
/// <remarks>
/// The executor is a system actor (§1): it calls the same shared transition functions as the request
/// paths, with no organizer-only authorization in front of them. It is idempotent — a re-run over an
/// already-tombstoned user repeats nothing and re-reports the original journal entry — and inert
/// before <c>GraceEndsAt</c> (D-148, §12-34): during the grace period the domain is untouched, so
/// the guard simply refuses to run.
/// </remarks>
public static class DeletionExecutor
{
    private static readonly UserDataScope[] AllDataScopes = Enum.GetValues<UserDataScope>();

    public static DeletionExecutionResult Execute(User user, DeletionScope scope, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(clock);

        RequireExecutable(user, clock);

        Instant now = clock.GetCurrentInstant();
        UserId subject = user.Id;
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

            // A deleted user must never be mapped into a session finalized later (D-092); a poll
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

        // 4. Friendships (accepted and still-pending requests alike) and blocks in both directions.
        List<Friendship> deletedFriendships = [];
        foreach (Friendship friendship in scope.Friendships)
        {
            if (friendship.Involves(subject))
            {
                deletedFriendships.Add(friendship);
            }
        }

        List<Block> deletedBlocks = [];
        foreach (Block block in scope.Blocks)
        {
            if (block.Involves(subject))
            {
                deletedBlocks.Add(block);
            }
        }

        // 6. Tombstone: the username enters its thirty-day, owner-less reservation (D-145, §12-36)
        //    and the User row nulls its PII while staying FK-stable.
        UsernameRegistryEntry? reservedUsername = null;
        if (scope.UsernameEntry is { IsActive: true } entry && entry.OwnerId == subject)
        {
            reservedUsername = UsernameRegistry.Release(entry, subject, clock);
        }

        user.MarkDeleted(clock);

        // 7. The A-017 journal entry. On a re-run this is the tombstone's original instant, so the
        //    journal never gains a second row for the same execution.
        DeletionJournalEntry journal = new(subject, user.DeletedAt ?? now);

        return new DeletionExecutionResult(
            canceledSessions,
            expiredPolls,
            removedFromSessions,
            removedFromPolls,
            lapsedJoinRequests,
            canceledReminders,
            deletedFriendships,
            deletedBlocks,
            AllDataScopes,
            reservedUsername,
            journal);
    }

    /// <summary>
    /// D-148/§12-34: nothing destructive happens before <c>GraceEndsAt</c>. A tombstone is allowed
    /// through so a repeated sweep is a no-op rather than an error.
    /// </summary>
    private static void RequireExecutable(User user, IClock clock)
    {
        if (user.IsTombstoned)
        {
            return;
        }

        if (!user.IsPendingDeletion)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant(
                    $"Deletion is executed for a PendingDeletion user, but this one is {user.Lifecycle}."));
        }

        if (!user.IsGraceElapsed(clock))
        {
            throw new DomainException(
                DomainError.WindowClosed,
                FormattableString.Invariant(
                    $"The deletion grace period runs until {user.GraceEndsAt}; the domain stays untouched until then (D-148)."));
        }
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
