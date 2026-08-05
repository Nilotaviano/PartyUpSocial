using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Sessions;

public sealed record JoinRequestCommandResult(bool Succeeded, string? Code, Session? Session, JoinRequest? JoinRequest)
{
    public static JoinRequestCommandResult Ok(Session session, JoinRequest? request = null) =>
        new(true, null, session, request);

    public static JoinRequestCommandResult Failed(string code) => new(false, code, null, null);
}

/// <summary>
/// Request/withdraw/list/approve/reject (D-098/D-107/D-173) plus waitlist promotion/rejection
/// (spec §7.1-9, §8.1) — PHASE4_DESIGN.md §5.2. Approval and rejection carry no attendance choice;
/// the affected participant's reminder handoff is replanned after every command that can move their
/// eligibility (§7.1).
/// </summary>
public sealed class SessionJoinRequestUseCases(
    ISessionRepository sessions,
    ISessionReadRepository reads,
    ISessionLockRepository sessionLocks,
    ISessionNotificationMaterializer materializer,
    ISessionReminderHandoffWriter reminders,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<JoinRequestCommandResult> RequestJoinAsync(
        SessionId sessionId,
        UserId requesterId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockUsersAsync([requesterId], ct);
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                JoinRequest request = session.OpenJoinRequest(requesterId, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                return JoinRequestCommandResult.Ok(session, request);
            },
            cancellationToken);

    public Task<JoinRequestCommandResult> WithdrawJoinRequestAsync(
        SessionId sessionId,
        UserId requesterId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockUsersAsync([requesterId], ct);
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                JoinRequest? request = session.JoinRequests
                    .FirstOrDefault(r => r.RequesterId == requesterId && r.IsPending);

                if (request is null)
                {
                    // D-107: withdrawal is idempotent — no pending request is simply a no-op.
                    return JoinRequestCommandResult.Ok(session);
                }

                session.WithdrawJoinRequest(request.Id, clock);
                _ = session.DequeueEvents();

                return JoinRequestCommandResult.Ok(session, request);
            },
            cancellationToken);

    /// <summary>Organizer-only; the same 404 covers a missing session and a non-organizer caller (D-007).</summary>
    public async Task<(bool Found, SessionPage<JoinRequestSummary> Page)> ListSessionJoinRequestsAsync(
        SessionId sessionId,
        UserId actorId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Session? session = await sessions.FindAsync(sessionId, cancellationToken);
        if (session is null || session.OrganizerId != actorId)
        {
            return (false, new SessionPage<JoinRequestSummary>([], null));
        }

        SessionPage<JoinRequestSummary> page = await reads.ListJoinRequestsAsync(
            sessionId, cursor, pageSize, cancellationToken);
        return (true, page);
    }

    public Task<JoinRequestCommandResult> ApproveJoinRequestAsync(
        SessionId sessionId,
        UserId organizerId,
        JoinRequestId joinRequestId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockUsersAsync([organizerId], ct);
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                if (session.OrganizerId != organizerId)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                JoinRequest? request = session.JoinRequests.FirstOrDefault(r => r.Id == joinRequestId);
                UserId? requesterId = request?.RequesterId;

                session.ApproveJoinRequest(joinRequestId, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                if (requesterId is { } id)
                {
                    await reminders.ReplanAsync(session, id, ct);
                }

                return JoinRequestCommandResult.Ok(session, request);
            },
            cancellationToken);

    public Task<JoinRequestCommandResult> RejectJoinRequestAsync(
        SessionId sessionId,
        UserId organizerId,
        JoinRequestId joinRequestId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockUsersAsync([organizerId], ct);
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                if (session.OrganizerId != organizerId)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                session.RejectJoinRequest(joinRequestId, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                return JoinRequestCommandResult.Ok(session);
            },
            cancellationToken);

    /// <summary>Spec §7.1-9: the organizer fills a free slot immediately; nothing reserves it beforehand.</summary>
    public Task<SessionCommandResult> PromoteWaitlistParticipantAsync(
        SessionId sessionId,
        UserId organizerId,
        ParticipationId participationId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockUsersAsync([organizerId], ct);
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return SessionCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                if (session.OrganizerId != organizerId)
                {
                    return SessionCommandResult.Failed("session_not_found");
                }

                Participation participation = RequireParticipation(session, participationId);
                session.PromoteFromWaitlist(participation.Participant, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                if (participation.UserId is { } userId)
                {
                    await reminders.ReplanAsync(session, userId, ct);
                }

                return SessionCommandResult.Ok(session);
            },
            cancellationToken);

    public Task<SessionCommandResult> RejectWaitlistParticipantAsync(
        SessionId sessionId,
        UserId organizerId,
        ParticipationId participationId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockUsersAsync([organizerId], ct);
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return SessionCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                if (session.OrganizerId != organizerId)
                {
                    return SessionCommandResult.Failed("session_not_found");
                }

                Participation participation = RequireParticipation(session, participationId);
                session.RejectFromWaitlist(participation.Participant, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                if (participation.UserId is { } userId)
                {
                    await reminders.ReplanAsync(session, userId, ct);
                }

                return SessionCommandResult.Ok(session);
            },
            cancellationToken);

    private static Participation RequireParticipation(Session session, ParticipationId participationId) =>
        session.Participations.FirstOrDefault(p => p.Id == participationId)
            ?? throw new DomainException(DomainError.NotFound, "No such participation on this session.");
}
