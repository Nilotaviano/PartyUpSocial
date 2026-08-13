using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Sessions;

public sealed record SetSessionReminderResult(bool Succeeded, string? Code, Session? Session)
{
    public static SetSessionReminderResult Ok(Session session) => new(true, null, session);

    public static SetSessionReminderResult Failed(string code) => new(false, code, null);
}

/// <summary>
/// D-082's per-session <c>ReminderChoice</c> override (PHASE4_DESIGN.md §5.2). Rejected with
/// <c>reminder_not_eligible</c> for a guest, a <c>Removed</c>/<c>Declined</c> row, a missing
/// participation, or a terminal session; otherwise a §7.1 replan trigger. Access runs through
/// <see cref="SessionActorVisibilityGate"/> first, so a caller the visibility engine denies gets the
/// same <c>session_not_found</c> a missing session gives (A-055).
/// </summary>
public sealed class SetSessionReminderUseCase(
    ISessionRepository sessions,
    ISessionLockRepository sessionLocks,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    ISessionReminderHandoffWriter reminders,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<SetSessionReminderResult> ExecuteAsync(
        SessionId sessionId,
        UserId actorId,
        ReminderOffset? choice,
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
                    return SetSessionReminderResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;

                Participation? row = session.Find(ParticipantRef.ForUser(actorId));
                if (row is null || row.IsRemoved || row.Status == ParticipationStatus.Declined)
                {
                    return SetSessionReminderResult.Failed("reminder_not_eligible");
                }

                if (session.IsTerminal)
                {
                    return SetSessionReminderResult.Failed("reminder_not_eligible");
                }

                session.SetReminderChoice(actorId, choice, clock);
                await reminders.ReplanAsync(session, actorId, ct);

                return SetSessionReminderResult.Ok(session);
            },
            cancellationToken);
}
