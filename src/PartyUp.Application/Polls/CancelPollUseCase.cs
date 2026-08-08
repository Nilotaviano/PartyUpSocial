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
