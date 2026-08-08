using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Application.Polls;

/// <summary>
/// Organizer cancellation, pre-finalization (PHASE6_DESIGN.md §2 "Cancel", DOMAIN_MODEL §6.1):
/// <see cref="Poll.Cancel"/> applies due transitions first (so a logically-expired poll can no
/// longer be canceled, §1), revokes every guest link <c>RevokedByCancel</c>, lapses pending join
/// requests, and raises the D-166 <see cref="PollCanceled"/> event — a single A-016 transaction with
/// the organizer-only, single-actor lock order (<c>PollOptionUseCases.DeleteOptionAsync</c>'s idiom:
/// users, then the poll parent).
/// </summary>
public sealed class CancelPollUseCase(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IPollNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<PollCommandResult> CancelAsync(
        PollId pollId,
        UserId organizerId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await pollLocks.LockUsersAsync([organizerId], ct);
                await pollLocks.LockPollsAsync([pollId], ct);

                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return PollCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                if (poll.OrganizerId != organizerId)
                {
                    return PollCommandResult.Failed("poll_not_found");
                }

                // A cancel attempted from the 48-hour selection window's close onward gets the same
                // design-named poll_selection_window_closed Finalize does, instead of Poll.Cancel's
                // own due-transition-then-status-guard path (which would surface the generic
                // poll_invalid_transition). Scoped to the two statuses this window can actually still
                // affect, so an already decision-terminal poll (Finalized/Canceled) keeps its own
                // domain-driven code. The due Expire transition still applies and materializes here
                // (ApplyDueTransitions, same call Poll.Cancel itself would have made) so a failed
                // command never skips the materialized-status invariant's own due transition (§1) --
                // only the reported problem code changes.
                if (poll.Status is PollStatus.Polling or PollStatus.Closed
                    && clock.GetCurrentInstant() >= poll.SelectionWindowEndsAt)
                {
                    poll.ApplyDueTransitions(clock);
                    IReadOnlyList<IDomainEvent> dueEvents = poll.DequeueEvents();
                    await materializer.MaterializeAsync(poll, dueEvents, ct);

                    return PollCommandResult.Failed("poll_selection_window_closed");
                }

                try
                {
                    poll.Cancel(clock);
                }
                catch (DomainException exception)
                {
                    return PollCommandResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return PollCommandResult.Ok(poll);
            },
            cancellationToken);
}
