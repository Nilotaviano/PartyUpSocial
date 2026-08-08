using Hangfire;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;

namespace PartyUp.Infrastructure.Jobs;

/// <summary>
/// The Hangfire recurring D-120 "deadline approaching" job (PHASE6_DESIGN.md §3, DOMAIN_MODEL:227):
/// a scheduled emission, not a transition, so it carries no domain event and never touches
/// <see cref="Poll.Status"/> — the durable-attempt discipline it follows is
/// <see cref="SessionReminderDueJob"/>'s (one transaction per candidate, decision re-checked fresh
/// under the poll's own lock rather than trusted from discovery). Discovery is unlocked
/// (<see cref="PollDueCandidateStore.GetDeadlineApproachingCandidatesAsync"/>); every candidate is
/// then locked and reloaded, <see cref="PollDeadlineNotice.ShouldSend"/> is re-evaluated against the
/// locked row (a poll that left <c>Polling</c> or whose deadline moved in the unlocked gap is
/// skipped, §10), and the recipient set
/// <see cref="RecipientComputer.DeadlineApproaching(Poll, RecipientFilter, IClock)"/> computes is
/// written through <see cref="PollDueCandidateStore.InsertDeadlineApproachingNotificationIfAbsentAsync"/>
/// — the <c>ux_notifications_poll_deadline_once</c> partial unique index, not application logic, is
/// what makes a second run over the same poll a no-op.
/// </summary>
public sealed class PollDeadlineApproachingJob(
    PollDueCandidateStore candidates,
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    /// <summary>Mirrors <see cref="SessionDueSweepJob.ExecuteAsync"/>'s overlap contract (A-066).</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Instant now = clock.GetCurrentInstant();
        IReadOnlyList<PollId> due =
            await candidates.GetDeadlineApproachingCandidatesAsync(now, cancellationToken);

        // One transaction per poll (mirrors SessionReminderDueJob's per-session grouping) — a poll's
        // own lock is the only lock this job ever needs to hold.
        foreach (PollId pollId in due)
        {
            await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    await pollLocks.LockPollsAsync([pollId], ct);

                    PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                    if (forUpdate is null)
                    {
                        return true;
                    }

                    Poll poll = forUpdate.Poll;
                    if (!PollDeadlineNotice.ShouldSend(poll, clock))
                    {
                        // Left Polling, or the deadline itself passed, since discovery (§10) — no
                        // notice, and never a late one.
                        return true;
                    }

                    RecipientFilter filter = RecipientFilter.Create(MuteSet.Create(forUpdate.Mutes));
                    IReadOnlyList<NotificationRequest> requests =
                        RecipientComputer.DeadlineApproaching(poll, filter, clock);

                    foreach (NotificationRequest request in requests)
                    {
                        await candidates.InsertDeadlineApproachingNotificationIfAbsentAsync(
                            poll.Id, request.RecipientUserId, now, ct);
                    }

                    return true;
                },
                cancellationToken);
        }
    }
}
