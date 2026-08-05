using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Sessions;

/// <summary>The A-070 closed clear-list members: fields with no other way to express "unset".</summary>
public enum SessionEditClearField
{
    MaxCapacity,
    Notes,
    Game,
}

public sealed record EditSessionCommand(
    SessionId SessionId,
    UserId ActorId,
    string? Title,
    Instant? StartTime,
    int? DurationMinutes,
    Platform? Platform,
    SessionGame? Game,
    string? Notes,
    int? MinAttendance,
    int? MaxCapacity,
    bool? IsPrivate,
    IReadOnlyCollection<SessionEditClearField>? Clear,
    /// <summary>
    /// A-071's opaque <c>version</c> token, already format-validated by the endpoint's
    /// <c>400 invalid_if_match</c> pre-check. Null only for internal callers (tests) that bypass HTTP
    /// and do not need the concurrency check; every HTTP-originated command supplies one.
    /// </summary>
    string? IfMatch = null);

public sealed record EditSessionResult(bool Succeeded, string? Code, Session? Session)
{
    public static EditSessionResult Edited(Session session) => new(true, null, session);

    public static EditSessionResult Failed(string code) => new(false, code, null);
}

/// <summary>
/// Organizer-only (PHASE4_DESIGN.md §5.2). Start/duration changes are the only reschedule triggers —
/// title/game/notes/max/privacy stay silent (D-122/D-123) — and the reminder handoff is replanned for
/// every registered participant exactly when the start or duration actually moves.
/// </summary>
public sealed class EditSessionUseCase(
    ISessionRepository sessions,
    ISessionLockRepository sessionLocks,
    ISessionNotificationMaterializer materializer,
    ISessionReminderHandoffWriter reminders,
    IUnitOfWork unitOfWork,
    IClock clock,
    ISessionVersionStamp versionStamp)
{
    public async Task<EditSessionResult> ExecuteAsync(EditSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        IReadOnlyCollection<SessionEditClearField> clear = command.Clear ?? [];
        if ((clear.Contains(SessionEditClearField.MaxCapacity) && command.MaxCapacity is not null)
            || (clear.Contains(SessionEditClearField.Notes) && command.Notes is not null)
            || (clear.Contains(SessionEditClearField.Game) && command.Game is not null))
        {
            return EditSessionResult.Failed("session_validation");
        }

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockUsersAsync([command.ActorId], ct);
                await sessionLocks.LockSessionsAsync([command.SessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(command.SessionId, ct);
                if (forUpdate is null)
                {
                    return EditSessionResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                if (session.OrganizerId != command.ActorId)
                {
                    return EditSessionResult.Failed("session_not_found");
                }

                // A-071: the comparison point is this freshly locked row's own xmin, not any earlier,
                // unlocked read — command.IfMatch is null only for internal callers that bypass HTTP.
                if (command.IfMatch is { } ifMatch && !versionStamp.Matches(session, ifMatch))
                {
                    return EditSessionResult.Failed("session_conflict");
                }

                bool reschedule = false;

                if (command.StartTime is { } startTime && startTime != session.StartTime)
                {
                    session.ChangeStartTime(startTime, clock);
                    reschedule = true;
                }

                if (command.DurationMinutes is { } durationMinutes)
                {
                    Duration15 duration = Duration15.FromMinutes(durationMinutes);
                    if (duration != session.Duration)
                    {
                        session.ChangeDuration(duration, clock);
                        reschedule = true;
                    }
                }

                int? desiredMax = clear.Contains(SessionEditClearField.MaxCapacity)
                    ? null
                    : command.MaxCapacity ?? session.MaxCapacity;
                if (desiredMax != session.MaxCapacity)
                {
                    session.ChangeMaxCapacity(desiredMax, clock);
                }

                if (command.MinAttendance is { } minAttendance && minAttendance != session.MinAttendance)
                {
                    session.ChangeMinAttendance(minAttendance, clock);
                }

                if (command.Title is { } title)
                {
                    session.ChangeTitle(SessionTitle.Create(title), clock);
                }

                bool gameChanged = clear.Contains(SessionEditClearField.Game) || command.Game is not null;
                if (gameChanged || command.Platform is not null)
                {
                    Platform platform = command.Platform ?? session.Platform;
                    SessionGame? game = clear.Contains(SessionEditClearField.Game) ? null : command.Game ?? session.Game;
                    session.ChangeGame(platform, game, clock);
                }

                if (clear.Contains(SessionEditClearField.Notes))
                {
                    session.ChangeNotes(SessionNotes.Empty, clock);
                }
                else if (command.Notes is { } notes)
                {
                    session.ChangeNotes(SessionNotes.Create(notes), clock);
                }

                if (command.IsPrivate is { } isPrivate)
                {
                    session.ChangePrivacy(isPrivate, clock);
                }

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                if (reschedule)
                {
                    foreach (Participation participation in session.Participations)
                    {
                        if (participation.UserId is { } userId)
                        {
                            await reminders.ReplanAsync(session, userId, ct);
                        }
                    }
                }

                return EditSessionResult.Edited(session);
            },
            cancellationToken);
    }

    /// <summary>
    /// D-156/D-157: due transitions apply before the guard, so a logically-expired session cannot be
    /// canceled and a logically-completed one cancels as Completed; Canceled is idempotent. Cancellation
    /// revokes every guest link (D-174), lapses pending join requests, and notifies the access-scoped
    /// audience minus the organizer (D-166) — the domain's own <see cref="Session.Cancel"/> raises that
    /// event.
    /// </summary>
    public Task<EditSessionResult> CancelAsync(
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
                    return EditSessionResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                if (session.OrganizerId != actorId)
                {
                    return EditSessionResult.Failed("session_not_found");
                }

                session.Cancel(clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                foreach (Participation participation in session.Participations)
                {
                    if (participation.UserId is { } userId)
                    {
                        await reminders.ReplanAsync(session, userId, ct);
                    }
                }

                return EditSessionResult.Edited(session);
            },
            cancellationToken);
}
