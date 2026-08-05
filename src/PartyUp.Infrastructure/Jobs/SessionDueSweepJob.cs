using Hangfire;
using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Jobs;

/// <summary>
/// The Hangfire recurring sweep (PHASE4_DESIGN.md §7.2): a session no request has touched since its end
/// time passed still needs its due transition applied, so the completed/expired status is materialized
/// before the next read or command relies on it (A-067). Discovery is the §4.1 bounded predicate, capped
/// at 500 sessions per tick; each candidate is applied through the same shared
/// <see cref="SessionTransitionUseCase"/> every request path uses, in its own transaction, so one wide
/// sweep transaction never holds a parent lock across the whole minute's batch.
/// </summary>
public sealed class SessionDueSweepJob(
    SessionDueCandidateStore candidates,
    SessionTransitionUseCase transitions,
    IClock clock)
{
    /// <summary>
    /// Blocks up to 55 s waiting for an overlapping tick to finish and then throws (A-066) — the failed
    /// job is the intended overlap signal, and the idempotent transition makes the next cadence's retry
    /// safe. Discovery and application are both idempotent, so an overlap can never duplicate a transition.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Instant now = clock.GetCurrentInstant();
        IReadOnlyList<SessionId> dueSessions =
            await candidates.GetDueSweepCandidatesAsync(now, cancellationToken);

        foreach (SessionId sessionId in dueSessions)
        {
            await transitions.ExecuteAsync(sessionId, cancellationToken);
        }
    }
}
