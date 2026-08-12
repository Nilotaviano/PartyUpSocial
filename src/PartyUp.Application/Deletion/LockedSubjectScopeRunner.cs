using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Deletion;

public enum LockedScopeOutcome
{
    /// <summary>The operation ran and its transaction committed.</summary>
    Executed = 0,

    /// <summary>No such user row.</summary>
    SubjectMissing = 1,

    /// <summary>The subject exists but the caller's eligibility predicate refused it.</summary>
    SubjectIneligible = 2,

    /// <summary>
    /// The candidate set never stabilized across the bounded retry, so nothing was written
    /// (PHASE4_DESIGN.md §6.2).
    /// </summary>
    RetriesExhausted = 3,
}

/// <summary>
/// What a <see cref="LockedSubjectScopeRunner"/> run does once the lock envelope has the subject and
/// its scope in hand. <see cref="OnSubjectLockedAsync"/> runs after the union lock and the tracked
/// load but <em>before</em> <see cref="IsEligible"/> — the D-180 ban path bans the subject there, so
/// its own "must be banned" predicate then passes; deletion supplies no hook at all.
/// </summary>
public sealed record LockedSubjectScopeOperation(
    Func<User, bool> IsEligible,
    Func<User, DeletionScope, CancellationToken, Task> ExecuteAsync)
{
    public Func<User, CancellationToken, Task>? OnSubjectLockedAsync { get; init; }

    /// <summary>Post-commit tail, run only for <see cref="LockedScopeOutcome.Executed"/>.</summary>
    public Func<CancellationToken, Task>? OnCommittedAsync { get; init; }
}

/// <summary>
/// The §6.2 pre-lock/reload protocol, shared by every command that mutates a user's whole content
/// scope (PHASE8_DESIGN.md §3 S2): discover the complete affected user set with no locks held, then
/// lock the deduplicated union of the subject and every discovered counterpart in <strong>one</strong>
/// A-068-ordered statement — never the subject alone followed by a lower-ordered counterpart, which is
/// exactly the inversion §6.2 forbids. <see cref="IUserRepository.FindForUpdateAsync"/> runs only
/// *after* that statement, to load the tracked entity; re-locking an already-locked row in the same
/// transaction is harmless. The scope is then reloaded, and the whole transaction aborts if the
/// reload's recomputed candidate set differs from what was discovered. A changed set cannot retry
/// inside the poisoned transaction, so it surfaces as <see cref="DeletionScopeChangedException"/> and
/// <see cref="RunAsync"/> retries the entire attempt — discovery included — in a fresh transaction, at
/// most 3 attempts with a jittered 20/50/100 ms backoff. <paramref name="sessionLocks"/> defaults to
/// <see langword="null"/> for callers that construct this type without the widened session/poll scope
/// (the bulk lock is simply skipped, falling back to the single-row subject lock); production
/// resolution always supplies it through DI.
/// </summary>
public sealed class LockedSubjectScopeRunner(
    IUserRepository users,
    IDeletionScopeProvider scopeProvider,
    IUnitOfWork unitOfWork,
    ISessionLockRepository? sessionLocks = null)
{
    private const int MaxAttempts = 3;
    private static readonly int[] BackoffFloorsMilliseconds = [20, 50, 100];

    public async Task<LockedScopeOutcome> RunAsync(
        UserId subject,
        LockedSubjectScopeOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                LockedScopeOutcome outcome = await unitOfWork.ExecuteAsync(
                    ct => RunOnceAsync(subject, operation, ct),
                    cancellationToken);

                if (outcome == LockedScopeOutcome.Executed && operation.OnCommittedAsync is not null)
                {
                    await operation.OnCommittedAsync(cancellationToken);
                }

                return outcome;
            }
            catch (DeletionScopeChangedException)
            {
                if (attempt >= MaxAttempts)
                {
                    break;
                }

                await Task.Delay(JitteredBackoff(attempt), cancellationToken);
            }
        }

        // Exhaustion abandons this subject for this attempt; nothing was written, and the caller
        // (a sweep cadence, or an admin who can retry) decides what to do about it.
        return LockedScopeOutcome.RetriesExhausted;
    }

    private async Task<LockedScopeOutcome> RunOnceAsync(
        UserId subject,
        LockedSubjectScopeOperation operation,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<UserId> discovered =
            await scopeProvider.DiscoverAffectedUserIdsAsync(subject, cancellationToken);

        if (sessionLocks is not null)
        {
            HashSet<UserId> union = [subject, .. discovered];
            await sessionLocks.LockUsersAsync(union, cancellationToken);
        }

        User? user = await users.FindForUpdateAsync(subject, cancellationToken);
        if (user is null)
        {
            return LockedScopeOutcome.SubjectMissing;
        }

        if (operation.OnSubjectLockedAsync is not null)
        {
            await operation.OnSubjectLockedAsync(user, cancellationToken);
        }

        if (!operation.IsEligible(user))
        {
            return LockedScopeOutcome.SubjectIneligible;
        }

        DeletionScope scope = await scopeProvider.LoadForUpdateAsync(subject, cancellationToken);
        IReadOnlyCollection<UserId> recomputed = IDeletionScopeProvider.ExtractAffectedUserIds(subject, scope);
        if (!SetsEqual(discovered, recomputed))
        {
            throw new DeletionScopeChangedException(subject);
        }

        await operation.ExecuteAsync(user, scope, cancellationToken);
        return LockedScopeOutcome.Executed;
    }

    private static bool SetsEqual(IReadOnlyCollection<UserId> first, IReadOnlyCollection<UserId> second) =>
        first.Count == second.Count && first.ToHashSet().SetEquals(second);

    private static TimeSpan JitteredBackoff(int attempt)
    {
        int floor = BackoffFloorsMilliseconds[Math.Min(attempt - 1, BackoffFloorsMilliseconds.Length - 1)];
        int jitterMilliseconds = Random.Shared.Next(0, floor / 2 + 1);
        return TimeSpan.FromMilliseconds(floor + jitterMilliseconds);
    }
}
