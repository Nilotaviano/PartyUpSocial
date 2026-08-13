using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Sessions;

/// <summary>
/// The registered RSVP surface: <c>PUT .../my-participation</c> (D-089's response change, including
/// the in-progress Going↔Maybe carve-out) and <c>DELETE .../my-participation</c> (D-055's
/// before-start-only leave) — PHASE4_DESIGN.md §5.2. The two share the Going/Maybe→Declined domain
/// path but keep distinct idempotency and error contracts, as §8 requires.
/// </summary>
public sealed class SessionParticipationUseCases(
    ISessionRepository sessions,
    ISessionLockRepository sessionLocks,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    ISessionNotificationMaterializer materializer,
    ISessionReminderHandoffWriter reminders,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<SessionCommandResult> RespondToInvitationAsync(
        SessionId sessionId,
        UserId actorId,
        ParticipationStatus response,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                SessionForUpdate? forUpdate = await SessionActorVisibilityGate.LoadVisibleForUpdateAsync(
                    sessionId,
                    actorId,
                    sessions,
                    sessionLocks,
                    friendships,
                    blocks,
                    ct);
                if (forUpdate is null)
                {
                    return SessionCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                ParticipantRef participant = ParticipantRef.ForUser(actorId);

                switch (response)
                {
                    case ParticipationStatus.Going:
                    case ParticipationStatus.Maybe:
                        session.Rsvp(participant, response, clock);
                        break;

                    case ParticipationStatus.Declined:
                        RespondDeclined(session, participant, clock);
                        break;

                    default:
                        return SessionCommandResult.Failed("session_validation");
                }

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);
                await reminders.ReplanAsync(session, actorId, ct);

                return SessionCommandResult.Ok(session);
            },
            cancellationToken);

    public Task<SessionCommandResult> LeaveSessionAsync(
        SessionId sessionId,
        UserId actorId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockUsersAsync([actorId], ct);
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return SessionCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                ParticipantRef participant = ParticipantRef.ForUser(actorId);
                Participation? row = session.Find(participant);
                if (row is null)
                {
                    return SessionCommandResult.Failed("session_not_found");
                }

                if (row.Status == ParticipationStatus.Declined)
                {
                    // D-055: leaving is idempotent once already Declined.
                    return SessionCommandResult.Ok(session);
                }

                session.Leave(participant, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);
                await reminders.ReplanAsync(session, actorId, ct);

                return SessionCommandResult.Ok(session);
            },
            cancellationToken);

    /// <summary>
    /// D-089: {response: Declined} is a response change, not a leave, but it shares the
    /// Going/Maybe → Declined domain path with <see cref="LeaveSessionAsync"/>; a Pending row
    /// declines instead, and an already-Declined row is idempotent.
    /// </summary>
    private static void RespondDeclined(Session session, ParticipantRef participant, IClock clock)
    {
        Participation row = session.Find(participant)
            ?? throw new DomainException(DomainError.NotFound, "That participant is not on this session's roster.");

        if (row.Status == ParticipationStatus.Declined)
        {
            return;
        }

        if (row.Status == ParticipationStatus.Pending)
        {
            session.DeclineInvitation(participant, clock);
        }
        else
        {
            session.Leave(participant, clock);
        }
    }
}
