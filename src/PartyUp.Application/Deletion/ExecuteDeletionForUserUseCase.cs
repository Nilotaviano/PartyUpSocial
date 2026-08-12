using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Deletion;

/// <summary>
/// Grace-expiry deletion, run inside the shared <see cref="LockedSubjectScopeRunner"/> lock envelope
/// (§6.2's pre-lock/reload protocol with its bounded <see cref="DeletionScopeChangedException"/>
/// retry). This use case supplies the three things that make the envelope a deletion: the
/// PendingDeletion + elapsed-grace eligibility predicate, the body (executor, notification
/// materialization, applier, credential purge, journal append), and the post-commit NDJSON journal
/// ship. It installs no post-lock subject hook — deletion mutates the subject only inside the
/// executor. <paramref name="sessionLocks"/>, <paramref name="materializer"/> and
/// <paramref name="pollMaterializer"/> default to <see langword="null"/> for callers that construct
/// this type directly without the widened session/poll scope (the bulk lock and event materialization
/// are simply skipped, falling back to the single-row subject lock); production resolution always
/// supplies all three through DI.
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
    ISessionNotificationMaterializer? materializer = null,
    IPollNotificationMaterializer? pollMaterializer = null)
{
    private readonly LockedSubjectScopeRunner _runner =
        new(users, scopeProvider, unitOfWork, sessionLocks);

    public async Task<bool> ExecuteAsync(UserId userId, CancellationToken cancellationToken)
    {
        // Exhaustion abandons this user for this tick; the Phase 2 grace-expiry sweep's next cadence
        // re-discovers and retries, so no user is permanently stuck (PHASE4_DESIGN.md §6.2).
        LockedScopeOutcome outcome = await _runner.RunAsync(
            userId,
            new LockedSubjectScopeOperation(
                user => user.IsPendingDeletion && user.IsGraceElapsed(clock),
                (user, scope, ct) => ExecuteOnceAsync(user, scope, ct))
            {
                OnCommittedAsync = shipper.ShipPendingAsync,
            },
            cancellationToken);

        return outcome == LockedScopeOutcome.Executed;
    }

    private async Task ExecuteOnceAsync(User user, DeletionScope scope, CancellationToken cancellationToken)
    {
        UserId userId = user.Id;
        DeletionExecutionResult result = DeletionExecutor.Execute(user, scope, clock);

        if (materializer is not null)
        {
            foreach (Session session in scope.Sessions)
            {
                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                if (events.Count > 0)
                {
                    // DOMAIN_MODEL.md §11 step 5: the deleting user receives no notifications from
                    // steps 1-3 of their own deletion's derived Session events.
                    await materializer.MaterializeAsync(session, events, userId, cancellationToken);
                }
            }
        }

        if (pollMaterializer is not null)
        {
            foreach (Poll poll in scope.Polls)
            {
                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                if (events.Count > 0)
                {
                    // §11 step 2 / D-170: the owned-poll expiry notification, with the same
                    // deleting-user exclusion the session half above applies.
                    await pollMaterializer.MaterializeAsync(poll, events, userId, cancellationToken);
                }
            }
        }

        await resultApplier.ApplyAsync(result, userId, cancellationToken);
        await accounts.PurgeCredentialsAsync(userId, cancellationToken);
        journal.Append(result.Journal);
    }
}

/// <summary>
/// PHASE4_DESIGN.md §6.2: the reload's recomputed candidate user set differed from what discovery
/// locked, so the transaction performed no writes. The already-poisoned transaction cannot retry
/// itself; <see cref="LockedSubjectScopeRunner.RunAsync"/> is the outer, bounded retry.
/// </summary>
public sealed class DeletionScopeChangedException(UserId subject)
    : Exception($"The deletion candidate set for {subject} changed between discovery and reload.")
{
    public UserId Subject { get; } = subject;
}
