using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Visibility;

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
/// <remarks>
/// <see cref="RequestJoinAsync"/> and <see cref="ApproveJoinRequestAsync"/> attach a requester to the
/// session (a new registered counterpart), so both run the full <c>P4-LK-01</c> six-step sequence
/// (§6.1): resolve visibility/authorization without locking arbitrary ids, lock the affected user set
/// in one statement, lock the organizer/requester friendship and block rows in one statement per
/// table, lock the session parent and reload, re-evaluate block eligibility under the fresh locks, then
/// persist. A blocked pair — discovered either before or after the lock — returns the same
/// <c>session_not_found</c> a stranger gets (D-065); it is indistinguishable and writes nothing.
/// <see cref="RejectJoinRequestAsync"/> shares the block re-check (so a block-first race also yields
/// the indistinguishable 404 there) but never attaches anyone, so it locks only the block pair, not
/// the friendship. Withdraw/list/promote/reject-waitlist act on an already-attached participant or
/// requester and introduce no new counterpart, so they keep the session-parent-lock-before-occupancy
/// shape §6.1 requires of them without the graph lock.
/// </remarks>
public sealed class SessionJoinRequestUseCases(
    ISessionRepository sessions,
    ISessionReadRepository reads,
    ISessionLockRepository sessionLocks,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
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
                // Step 1: resolve authorization without locking arbitrary ids; hidden/missing -> 404.
                Session? probe = await sessions.FindNoTrackingAsync(sessionId, ct);
                if (probe is null || requesterId == probe.OrganizerId)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                Viewer earlyViewer = await BuildViewerAsync(requesterId, probe, ct);
                if (!VisibilityEngine.ResolveSession(probe, earlyViewer).IsVisible)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                UserId organizerId = probe.OrganizerId;

                // Step 2: lock the complete affected registered-user set in one statement.
                await sessionLocks.LockUsersAsync([organizerId, requesterId], ct);

                // Step 3: lock relevant friendship rows and both block directions, one statement per table.
                IReadOnlyList<Friendship> lockedFriendships =
                    await friendships.LockPairsAsync(organizerId, [requesterId], ct);
                IReadOnlyList<Block> lockedBlocks = await blocks.LockPairsAsync(organizerId, [requesterId], ct);

                // Step 4: lock the session parent, then load its children.
                await sessionLocks.LockSessionsAsync([sessionId], ct);
                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;

                // Step 5: re-evaluate visibility under the fresh locks. Rule 2 (DOMAIN_MODEL.md:184)
                // grants inherited access via ANY extending active registered participant, not just the
                // organizer -- the participant set is only known now that the parent lock has loaded the
                // roster, so a second bulk graph-lock statement (one per table, A-068 order) covers the
                // requester's pair with every extender. The pre-parent-lock organizer-pair statement
                // above stays as the direct-access/organizer-inheritance lock.
                HashSet<UserId> extenderIds = [];
                foreach (Participation participation in session.Participations)
                {
                    if (participation.UserId is { } participantId
                        && participantId != requesterId
                        && participantId != organizerId
                        && SessionVisibility.ExtendsAccess(participation))
                    {
                        extenderIds.Add(participantId);
                    }
                }

                IReadOnlyList<Friendship> extenderFriendships = extenderIds.Count == 0
                    ? []
                    : await friendships.LockPairsAsync(requesterId, extenderIds, ct);
                IReadOnlyList<Block> extenderBlocks = extenderIds.Count == 0
                    ? []
                    : await blocks.LockPairsAsync(requesterId, extenderIds, ct);

                // Recompute visibility with the SAME VisibilityEngine rules the read side uses -- no
                // hand-rolled subset. A block with the organizer yields NotFound (rule 4); a block with
                // one extender only removes that extender from consideration while others still count.
                Viewer viewer = Viewer.From(
                    requesterId,
                    [.. lockedFriendships, .. extenderFriendships],
                    [.. lockedBlocks, .. extenderBlocks]);
                if (!VisibilityEngine.ResolveSession(session, viewer).IsVisible)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                JoinRequest request = session.OpenJoinRequest(requesterId, clock);

                // Step 6: persist.
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
                // Step 1: resolve authorization without locking arbitrary ids; hidden/missing -> 404.
                Session? probe = await sessions.FindNoTrackingAsync(sessionId, ct);
                if (probe is null || probe.OrganizerId != organizerId)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                JoinRequest? probeRequest = probe.JoinRequests.FirstOrDefault(r => r.Id == joinRequestId);
                if (probeRequest is null)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                UserId requesterId = probeRequest.RequesterId;

                // Step 2: lock the complete affected registered-user set in one statement.
                await sessionLocks.LockUsersAsync([organizerId, requesterId], ct);

                // Step 3: lock relevant friendship rows and both block directions, one statement per table.
                IReadOnlyList<Friendship> lockedFriendships =
                    await friendships.LockPairsAsync(organizerId, [requesterId], ct);
                IReadOnlyList<Block> lockedBlocks = await blocks.LockPairsAsync(organizerId, [requesterId], ct);
                _ = lockedFriendships;

                // Step 4: lock the session parent, then load its children.
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

                // Step 5: re-evaluate under the fresh locks. Block-first (or a since-vanished session)
                // is indistinguishable from missing -- zero writes, zero notifications (D-065). Only
                // once the pair is proven unblocked does a non-pending request become its own,
                // distinct 409 (§8 join_request_not_pending) rather than a leaked domain exception.
                if (lockedBlocks.Any(b => b.Hides(organizerId, requesterId)))
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                JoinRequest? request = session.JoinRequests.FirstOrDefault(r => r.Id == joinRequestId);
                if (request is null)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                if (!request.IsPending)
                {
                    return JoinRequestCommandResult.Failed("join_request_not_pending");
                }

                session.ApproveJoinRequest(joinRequestId, clock);

                // Step 6: persist.
                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);
                await reminders.ReplanAsync(session, requesterId, ct);

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
                Session? probe = await sessions.FindNoTrackingAsync(sessionId, ct);
                if (probe is null || probe.OrganizerId != organizerId)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                JoinRequest? probeRequest = probe.JoinRequests.FirstOrDefault(r => r.Id == joinRequestId);
                if (probeRequest is null)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                UserId requesterId = probeRequest.RequesterId;

                await sessionLocks.LockUsersAsync([organizerId, requesterId], ct);
                IReadOnlyList<Block> lockedBlocks = await blocks.LockPairsAsync(organizerId, [requesterId], ct);

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

                // Block-first is indistinguishable from missing (D-065); only past that gate does a
                // non-pending request get its own distinct 409 rather than a leaked domain exception.
                if (lockedBlocks.Any(b => b.Hides(organizerId, requesterId)))
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                JoinRequest? request = session.JoinRequests.FirstOrDefault(r => r.Id == joinRequestId);
                if (request is null)
                {
                    return JoinRequestCommandResult.Failed("session_not_found");
                }

                if (!request.IsPending)
                {
                    return JoinRequestCommandResult.Failed("join_request_not_pending");
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

    /// <summary>
    /// The minimal, non-locking §7 viewer facts a pre-lock visibility resolve needs: the organizer plus
    /// every other registered participant, read in two bulk (not per-counterpart) queries — mirrors
    /// <see cref="SessionReadUseCases"/>'s viewer builder, proportionate for a single session (§6.1
    /// step 1; F6's fix applies the same shape to the read side).
    /// </summary>
    private async Task<Viewer> BuildViewerAsync(UserId viewerId, Session session, CancellationToken cancellationToken)
    {
        HashSet<UserId> counterparts = [session.OrganizerId];
        foreach (Participation participation in session.Participations)
        {
            if (participation.UserId is { } userId && userId != viewerId)
            {
                counterparts.Add(userId);
            }
        }

        counterparts.Remove(viewerId);

        if (counterparts.Count == 0)
        {
            return Viewer.Create(viewerId);
        }

        IReadOnlyList<Friendship> pairFriendships = await friendships.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);
        IReadOnlyList<Block> pairBlocks = await blocks.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);

        List<UserId> friendIds = [.. pairFriendships.Where(f => f.IsAccepted).Select(f => f.Counterpart(viewerId))];
        List<UserId> blockedIds = [.. pairBlocks.Select(b => b.BlockerId == viewerId ? b.BlockedId : b.BlockerId)];

        return Viewer.Create(viewerId, friendIds, blockedIds);
    }

    private static Participation RequireParticipation(Session session, ParticipationId participationId) =>
        session.Participations.FirstOrDefault(p => p.Id == participationId)
            ?? throw new DomainException(DomainError.NotFound, "No such participation on this session.");
}
