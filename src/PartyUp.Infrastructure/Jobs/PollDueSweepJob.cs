using Hangfire;
using NodaTime;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Jobs;

/// <summary>
/// The Hangfire recurring poll sweep (PHASE6_DESIGN.md §3), mirroring <see cref="SessionDueSweepJob"/>:
/// a poll no request has touched since its deadline (D-079) or its 48-hour selection window (D-088,
/// D-170) passed still needs its due transition applied, so the materialized status is correct before
/// the next read or command relies on it (A-067). Discovery is the bounded predicate
/// <see cref="PollDueCandidateStore.GetDueSweepCandidatesAsync"/> defines; each candidate is applied
/// through the same shared <see cref="PollTransitionUseCase"/> every request path's
/// <see cref="Domain.Polls.Poll.ApplyDueTransitions"/> call goes through, in its own transaction, so
/// one wide sweep transaction never holds a parent lock across the whole minute's batch. One job
/// covers both transitions — Close and Expire — because <c>ApplyDueTransitions</c> already does.
/// </summary>
public sealed class PollDueSweepJob(
    PollDueCandidateStore candidates,
    PollTransitionUseCase transitions,
    IClock clock)
{
    /// <summary>Mirrors <see cref="SessionDueSweepJob.ExecuteAsync"/>'s overlap contract (A-066).</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Instant now = clock.GetCurrentInstant();
        IReadOnlyList<PollId> duePolls =
            await candidates.GetDueSweepCandidatesAsync(now, cancellationToken);

        foreach (PollId pollId in duePolls)
        {
            await transitions.ExecuteAsync(pollId, cancellationToken);
        }
    }
}
