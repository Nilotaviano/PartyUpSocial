using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Application.Polls;

/// <summary>
/// The one shared, idempotent due-transition path for polls (A-067), mirroring
/// <see cref="Sessions.SessionTransitionUseCase"/>: every request-path command applies
/// <see cref="Poll.ApplyDueTransitions"/> first via the aggregate itself, and S3's
/// <c>PollDueSweepJob</c> calls this use case per-poll, in its own bounded transaction, to do the
/// same thing for a poll no request has touched since its deadline or selection window passed
/// (PHASE6_DESIGN.md §3).
/// </summary>
public sealed class PollTransitionUseCase(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IPollNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<Poll?> ExecuteAsync(PollId pollId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await pollLocks.LockPollsAsync([pollId], ct);

                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return null;
                }

                Poll poll = forUpdate.Poll;
                poll.ApplyDueTransitions(clock);

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                if (events.Count > 0)
                {
                    await materializer.MaterializeAsync(poll, events, ct);
                }

                return poll;
            },
            cancellationToken);
}
