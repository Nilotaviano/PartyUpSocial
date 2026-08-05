using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Sessions;

public sealed record SessionCommandResult(bool Succeeded, string? Code, Session? Session)
{
    public static SessionCommandResult Ok(Session session) => new(true, null, session);

    public static SessionCommandResult Failed(string code) => new(false, code, null);
}

public sealed record CreateGuestLinksResult(bool Succeeded, string? Code, IReadOnlyList<CreatedGuestLink>? GuestLinks)
{
    public static CreateGuestLinksResult Ok(IReadOnlyList<CreatedGuestLink> links) => new(true, null, links);

    public static CreateGuestLinksResult Failed(string code) => new(false, code, null);
}

/// <summary>
/// D-020's post-creation registered invitations, organizer-issued guest links, and the two revocation
/// paths (unclaimed-link revoke, claimed-guest removal) — PHASE4_DESIGN.md §5.2. Invite is
/// all-or-nothing: every invitee's eligibility is checked before any mutation, so a single unusable
/// invitee never partially attaches the rest and never discloses which invitee was blocked.
/// </summary>
public sealed class SessionInvitationUseCases(
    ISessionRepository sessions,
    ISessionLockRepository sessionLocks,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    ISessionNotificationMaterializer materializer,
    ISessionReminderHandoffWriter reminders,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public const int MaxInviteeCount = 50;
    public const int MaxGuestLinkCount = 20;

    public async Task<SessionCommandResult> InviteRegisteredUsersAsync(
        SessionId sessionId,
        UserId organizerId,
        IReadOnlyList<UserId> inviteeIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inviteeIds);

        List<UserId> invitees = [.. inviteeIds.Distinct()];
        if (invitees.Count is 0 or > MaxInviteeCount)
        {
            return SessionCommandResult.Failed("session_validation");
        }

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                HashSet<UserId> lockSet = [organizerId, .. invitees];
                await sessionLocks.LockUsersAsync(lockSet, ct);
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

                Dictionary<UserId, bool> blockedByInvitee = [];
                foreach (UserId inviteeId in invitees)
                {
                    Friendship? friendship = await friendships.FindPairForUpdateAsync(organizerId, inviteeId, ct);
                    IReadOnlyList<Block> pairBlocks = await blocks.FindPairForUpdateAsync(organizerId, inviteeId, ct);
                    bool blocked = pairBlocks.Count != 0;
                    blockedByInvitee[inviteeId] = blocked;

                    if (friendship?.IsAccepted != true || blocked)
                    {
                        return SessionCommandResult.Failed("user_not_found");
                    }
                }

                foreach (UserId inviteeId in invitees)
                {
                    session.Invite(inviteeId, clock, id => blockedByInvitee.GetValueOrDefault(id));
                }

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                foreach (UserId inviteeId in invitees)
                {
                    await reminders.ReplanAsync(session, inviteeId, ct);
                }

                return SessionCommandResult.Ok(session);
            },
            cancellationToken);
    }

    public Task<CreateGuestLinksResult> CreateGuestLinksAsync(
        SessionId sessionId,
        UserId organizerId,
        int count,
        CancellationToken cancellationToken)
    {
        if (count is < 1 or > MaxGuestLinkCount)
        {
            return Task.FromResult(CreateGuestLinksResult.Failed("session_validation"));
        }

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockUsersAsync([organizerId], ct);
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return CreateGuestLinksResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                if (session.OrganizerId != organizerId)
                {
                    return CreateGuestLinksResult.Failed("session_not_found");
                }

                List<CreatedGuestLink> links = [];
                for (int i = 0; i < count; i++)
                {
                    (GuestLink link, string token) = session.CreateGuestLink(clock);
                    links.Add(new CreatedGuestLink(link.Id, token));
                }

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                return CreateGuestLinksResult.Ok(links);
            },
            cancellationToken);
    }

    public Task<SessionCommandResult> RevokeGuestLinkAsync(
        SessionId sessionId,
        UserId organizerId,
        GuestLinkId guestLinkId,
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

                GuestLink? link = session.GuestLinks.FirstOrDefault(l => l.Id == guestLinkId);
                if (link is not null && !link.IsActive)
                {
                    // D-150: idempotent — an already-revoked link has no roster effect either way.
                    return SessionCommandResult.Ok(session);
                }

                session.RevokeGuestLink(guestLinkId, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                return SessionCommandResult.Ok(session);
            },
            cancellationToken);

    /// <summary>D-044: identifies the guest by participation id, never by bearer token or link id.</summary>
    public Task<SessionCommandResult> RemoveSessionGuestAsync(
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

                Participation? participation = session.Participations
                    .FirstOrDefault(p => p.Id == participationId);
                if (participation is null || !participation.IsGuest || participation.GuestIdentityId is not { } guestIdentityId)
                {
                    return SessionCommandResult.Failed("session_not_found");
                }

                session.RemoveGuest(guestIdentityId, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                return SessionCommandResult.Ok(session);
            },
            cancellationToken);
}
