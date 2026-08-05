using Hangfire;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Jobs;

/// <summary>
/// The Hangfire recurring reminder-delivery job (PHASE4_DESIGN.md §7.2, P4-REMIND-01). Discovery is
/// unlocked and reads no session/participation state; every decision below is made only after the
/// candidate's session parent and that session's whole <c>session_reminder_handoffs</c> row set are
/// locked (A-068), so a concurrent worker that discovered the same candidate blocks until this
/// transaction commits and then observes the row's changed state/version as stale (§7.2's "unchanged
/// since discovery" clause) rather than delivering twice.
/// </summary>
public sealed class SessionReminderDueJob(
    SessionDueCandidateStore candidates,
    ISessionRepository sessions,
    ISessionLockRepository sessionLocks,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    /// <summary>A row that reaches its fifth execution attempt without resolving is dead-lettered (§4.1).</summary>
    private const int MaxAttempts = 5;

    /// <summary>Mirrors <see cref="SessionDueSweepJob.ExecuteAsync"/>'s overlap contract (A-066).</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Instant now = clock.GetCurrentInstant();
        IReadOnlyList<DueReminderCandidate> due =
            await candidates.GetDueReminderCandidatesAsync(now, cancellationToken);

        // One transaction per session (§7.2), never one per tick: a session's handoffs are the only
        // rows this job ever locks together, so grouping by session is also the only grouping that
        // needs a shared lock scope.
        foreach (IGrouping<SessionId, DueReminderCandidate> group in due.GroupBy(candidate => candidate.SessionId))
        {
            SessionId sessionId = group.Key;
            List<DueReminderCandidate> sessionCandidates = [.. group];

            await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    await sessionLocks.LockSessionsAsync([sessionId], ct);
                    await candidates.LockSessionHandoffsAsync(sessionId, ct);

                    Session? session = await sessions.FindAsync(sessionId, ct);
                    if (session is null)
                    {
                        // The session was deleted/tombstoned between discovery and this lock — nothing
                        // left to notify or update.
                        return true;
                    }

                    foreach (DueReminderCandidate candidate in sessionCandidates)
                    {
                        await ProcessAsync(session, candidate, now, ct);
                    }

                    return true;
                },
                cancellationToken);
        }
    }

    private async Task ProcessAsync(
        Session session,
        DueReminderCandidate candidate,
        Instant now,
        CancellationToken cancellationToken)
    {
        SessionReminderHandoff? handoff = await candidates.FindHandoffAsync(candidate.HandoffId, cancellationToken);

        // Stale: resolved by a concurrent worker, canceled/replanned by a request path, or otherwise
        // changed since discovery — no-op, per §7.2's "version/state unchanged since discovery" clause.
        if (handoff is null
            || handoff.State != SessionReminderHandoffState.Pending
            || handoff.Version != candidate.Version)
        {
            return;
        }

        handoff.Attempts++;
        handoff.LastAttemptAt = now;

        if (handoff.Attempts >= MaxAttempts)
        {
            handoff.State = SessionReminderHandoffState.Failed;
            return;
        }

        Participation? participation = session.Find(ParticipantRef.ForUser(handoff.RecipientUserId));
        bool stillEligible = participation is not null && ReminderRules.IsEligible(participation);
        bool deliverable = now < session.StartTime && !session.IsTerminal && stillEligible;

        if (deliverable)
        {
            await candidates.InsertReminderNotificationIfAbsentAsync(
                handoff.Id,
                handoff.RecipientUserId,
                session.Id,
                now,
                cancellationToken);
            handoff.State = SessionReminderHandoffState.Delivered;
            handoff.DeliveredAt = now;
        }
        else
        {
            // now >= start_time, the session is terminal, or the participant is no longer eligible —
            // the DOMAIN_MODEL.md §9 "already past is skipped, never fired late" rule applied to the
            // session start, which is the instant the reminder was about.
            handoff.State = SessionReminderHandoffState.Canceled;
            handoff.DeliveredAt = null;
        }
    }
}
