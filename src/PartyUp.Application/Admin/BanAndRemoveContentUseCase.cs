using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Deletion;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Admin;

/// <summary>
/// D-180's optional content removal (PHASE8_DESIGN.md §3 S2): a ban plus DOMAIN_MODEL §11 steps 1–4
/// and nothing else — cancel the subject's not-started owned sessions, expire their live owned polls,
/// remove them from other people's not-started content, lapse their own pending join requests, cancel
/// the reminders those removals orphan, and (D-209) hard-delete every group the subject owns plus
/// their own membership row in every group someone else owns — the same group-severing step
/// <see cref="DeletionExecutor"/> runs, shared through <see cref="ContentRemovalExecutor"/> so the ban
/// path cannot fall behind account deletion's group effects. Friendships, blocks, Completed history,
/// PII, credentials, avatars, push subscriptions,
/// notifications, the username reservation and the A-017 journal are all deliberately untouched: the
/// account stays a normal, live, banned account, and an unban does not restore any of the removed
/// content (including groups — D-209 severing is not reversible by unban).
///
/// <para>
/// Both halves commit in <strong>one</strong> outermost <see cref="IUnitOfWork.ExecuteAsync"/>, run by
/// the shared <see cref="LockedSubjectScopeRunner"/>: a failed removal therefore leaves no ban behind.
/// <c>User.Ban</c> and the sign-out are applied through the runner's post-lock subject hook — on the
/// very entity its own <c>FindForUpdateAsync</c> returned, after the single A-068-ordered union lock —
/// never by nesting <see cref="BanUserUseCase.ExecuteAsync"/> (which would both invert the §6.2 lock
/// order by locking the subject first and lose the A-069 retry boundary to a nested
/// <c>ExecuteAsync</c>).
/// </para>
/// </summary>
public sealed class BanAndRemoveContentUseCase(
    IUserRepository users,
    IDeletionScopeProvider scopeProvider,
    IDeletionResultApplier resultApplier,
    IAccountSessionService sessions,
    IUnitOfWork unitOfWork,
    IClock clock,
    ISessionNotificationMaterializer materializer,
    IPollNotificationMaterializer pollMaterializer,
    ISessionLockRepository? sessionLocks = null)
{
    private readonly LockedSubjectScopeRunner _runner =
        new(users, scopeProvider, unitOfWork, sessionLocks);

    public async Task<AdminUserMutationResult> ExecuteAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        LockedScopeOutcome outcome = await _runner.RunAsync(
            userId,
            new LockedSubjectScopeOperation(
                user => user.IsBanned,
                (user, scope, ct) => RemoveContentAsync(user.Id, scope, ct))
            {
                OnSubjectLockedAsync = BanAsync,
            },
            cancellationToken);

        return outcome switch
        {
            LockedScopeOutcome.Executed => AdminUserMutationResult.Completed,
            LockedScopeOutcome.SubjectMissing => AdminUserMutationResult.Failed("not_found"),
            _ => AdminUserMutationResult.Failed("scope_contended"),
        };
    }

    private async Task BanAsync(User user, CancellationToken cancellationToken)
    {
        user.Ban(clock);
        await sessions.SignOutEverywhereAsync(user.Id, cancellationToken);
    }

    private async Task RemoveContentAsync(
        UserId subject,
        DeletionScope scope,
        CancellationToken cancellationToken)
    {
        ContentRemovalResult result = ContentRemovalExecutor.Execute(subject, scope, clock);

        foreach (Session session in scope.Sessions)
        {
            IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
            if (events.Count > 0)
            {
                // The subject is excluded exactly as the deletion path excludes the deleting
                // user, so counterparts read the ordinary cancellation/removal copy and the ban
                // itself is never disclosed.
                await materializer.MaterializeAsync(session, events, subject, cancellationToken);
            }
        }

        foreach (Poll poll in scope.Polls)
        {
            IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
            if (events.Count > 0)
            {
                await pollMaterializer.MaterializeAsync(poll, events, subject, cancellationToken);
            }
        }

        // A-016: the reminder cancellations each removal produced commit with the state change.
        await resultApplier.ApplyContentRemovalAsync(result, subject, cancellationToken);
    }
}
