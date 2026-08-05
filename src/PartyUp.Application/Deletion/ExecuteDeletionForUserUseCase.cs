using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Deletion;

/// <summary>
/// The §6.2 pre-lock/reload protocol: discover the complete affected user set with no locks held, then
/// lock the deduplicated union of the subject and every discovered counterpart in **one**
/// A-068-ordered statement — never the subject alone followed by a lower-ordered counterpart, which is
/// exactly the inversion §6.2 forbids. <see cref="IUserRepository.FindForUpdateAsync"/> runs only
/// *after* that statement, to load the tracked entity; re-locking an already-locked row in the same
/// transaction is harmless. The scope is then reloaded, and the whole transaction aborts if the
/// reload's recomputed candidate set differs from what was discovered. A changed set cannot retry
/// inside the poisoned transaction, so it surfaces as <see cref="DeletionScopeChangedException"/> and
/// <see cref="ExecuteAsync"/> retries the entire attempt — discovery included — in a fresh transaction,
/// at most 3 attempts with a jittered 20/50/100 ms backoff. <paramref name="sessionLocks"/> and
/// <paramref name="materializer"/> default to <see langword="null"/> for callers that construct this
/// type directly without the widened session scope (the bulk lock and session-event materialization are
/// simply skipped, falling back to the single-row subject lock); production resolution always supplies
/// both through DI.
/// </summary>
public sealed class ExecuteDeletionForUserUseCase(
    IUserRepository users,
    IDeletionScopeProvider scopeProvider,
    IDeletionResultApplier resultApplier,
    IUserAccountGateway accounts,
    IDeletionJournal journal,
    IDeletionJournalShipper shipper,
    IUnitOfWork unitOfWork,
    IClock clock,
    ISessionLockRepository? sessionLocks = null,
    ISessionNotificationMaterializer? materializer = null)
{
    private const int MaxAttempts = 3;
    private static readonly int[] BackoffFloorsMilliseconds = [20, 50, 100];

    public async Task<bool> ExecuteAsync(UserId userId, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                bool executed = await unitOfWork.ExecuteAsync(
                    ct => ExecuteOnceAsync(userId, ct),
                    cancellationToken);

                if (executed)
                {
                    await shipper.ShipPendingAsync(cancellationToken);
                }

                return executed;
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

        // Exhaustion abandons this user for this tick; the Phase 2 grace-expiry sweep's next cadence
        // re-discovers and retries, so no user is permanently stuck (PHASE4_DESIGN.md §6.2).
        return false;
    }

    private async Task<bool> ExecuteOnceAsync(UserId userId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<UserId> discovered =
            await scopeProvider.DiscoverAffectedUserIdsAsync(userId, cancellationToken);

        if (sessionLocks is not null)
        {
            HashSet<UserId> union = [userId, .. discovered];
            await sessionLocks.LockUsersAsync(union, cancellationToken);
        }

        User? user = await users.FindForUpdateAsync(userId, cancellationToken);
        if (user is null || !user.IsPendingDeletion || !user.IsGraceElapsed(clock))
        {
            return false;
        }

        DeletionScope scope = await scopeProvider.LoadForUpdateAsync(userId, cancellationToken);
        IReadOnlyCollection<UserId> recomputed = IDeletionScopeProvider.ExtractAffectedUserIds(userId, scope);
        if (!SetsEqual(discovered, recomputed))
        {
            throw new DeletionScopeChangedException(userId);
        }

        DeletionExecutionResult result = DeletionExecutor.Execute(user, scope, clock);

        if (materializer is not null)
        {
            foreach (Session session in scope.Sessions)
            {
                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                if (events.Count > 0)
                {
                    await materializer.MaterializeAsync(session, events, cancellationToken);
                }
            }
        }

        await resultApplier.ApplyAsync(result, userId, cancellationToken);
        await accounts.PurgeCredentialsAsync(userId, cancellationToken);
        journal.Append(result.Journal);
        return true;
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

/// <summary>
/// PHASE4_DESIGN.md §6.2: the reload's recomputed candidate user set differed from what discovery
/// locked, so the transaction performed no writes. The already-poisoned transaction cannot retry
/// itself; <see cref="ExecuteDeletionForUserUseCase.ExecuteAsync"/> is the outer, bounded retry.
/// </summary>
public sealed class DeletionScopeChangedException(UserId subject)
    : Exception($"The deletion candidate set for {subject} changed between discovery and reload.")
{
    public UserId Subject { get; } = subject;
}
