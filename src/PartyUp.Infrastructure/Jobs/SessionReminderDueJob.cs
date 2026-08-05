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
    /// <summary>
    /// A row still not deliverable on its fifth recorded execution attempt is dead-lettered instead of
    /// canceled (§4.1). Delivery is always attempted normally regardless of attempt count — a row that
    /// is genuinely deliverable on its fifth attempt still delivers; only a fifth attempt that also
    /// fails to be deliverable is dead-lettered.
    /// </summary>
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

            // F3: the attempt is recorded in its own committed statement, with no ambient transaction,
            // BEFORE the per-session transaction below even opens — so a later crash/rollback in that
            // transaction still leaves the attempt durably recorded rather than erased. A row already
            // resolved or replanned since discovery (guard mismatch) is dropped here rather than
            // processed further.
            List<(DueReminderCandidate Candidate, int Attempts)> attempted = [];
            foreach (DueReminderCandidate candidate in sessionCandidates)
            {
                int? recordedAttempts = await candidates.RecordAttemptAsync(
                    candidate.HandoffId, candidate.Version, now, cancellationToken);
                if (recordedAttempts is int recorded)
                {
                    attempted.Add((candidate, recorded));
                }
            }

            if (attempted.Count == 0)
            {
                continue;
            }

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

                    foreach ((DueReminderCandidate candidate, int recordedAttempts) in attempted)
                    {
                        await ProcessAsync(session, candidate, recordedAttempts, now, ct);
                    }

                    return true;
                },
                cancellationToken);
        }
    }

    private async Task ProcessAsync(
        Session session,
        DueReminderCandidate candidate,
        int recordedAttempts,
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

        if (handoff.DueAt > now)
        {
            // A replan landed in the unlocked gap between discovery and this lock and pushed the due
            // instant into the future while leaving state/version looking unchanged at this candidate's
            // captured snapshot (F1) — leave it Pending for its own future due_at rather than deliver
            // early or cancel a row that has not actually come due.
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
            return;
        }

        // now >= start_time, the session is terminal, or the participant is no longer eligible — the
        // DOMAIN_MODEL.md §9 "already past is skipped, never fired late" rule applied to the session
        // start, which is the instant the reminder was about. §4.1/A-066: attempts is recorded durably
        // before this transaction opens (see ExecuteAsync), so reaching the fifth recorded attempt here
        // means this row has genuinely failed to resolve five times over — dead-letter it instead of
        // canceling it silently forever.
        if (recordedAttempts >= MaxAttempts)
        {
            handoff.State = SessionReminderHandoffState.Failed;
            handoff.DeliveredAt = null;
            return;
        }

        handoff.State = SessionReminderHandoffState.Canceled;
        handoff.DeliveredAt = null;
    }
}
