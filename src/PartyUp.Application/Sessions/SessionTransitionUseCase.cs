using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Sessions;

/// <summary>
/// The one shared, idempotent due-transition path (A-067): every request-path command applies it
/// first via the aggregate's own <see cref="Session.ApplyDueTransitions"/>, and S3's sweeper job calls
/// this use case per-session, in its own bounded transaction, to do the same thing for a session no
/// request has touched since its end time passed (PHASE4_DESIGN.md §7.2).
/// </summary>
public sealed class SessionTransitionUseCase(
    ISessionRepository sessions,
    ISessionLockRepository sessionLocks,
    ISessionNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<Session?> ExecuteAsync(SessionId sessionId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return null;
                }

                Session session = forUpdate.Session;
                session.ApplyDueTransitions(clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                if (events.Count > 0)
                {
                    await materializer.MaterializeAsync(session, events, ct);
                }

                return session;
            },
            cancellationToken);
}
