using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Sessions;

/// <summary>
/// D-225: the signed-in, onboarded holder of a live guest credential links that seat to their
/// account. Same parent-lock/one-transaction path as every other guest write
/// (<see cref="GuestSessionUseCases"/>) — the session row is locked before occupancy is read, since
/// the duplicate branch frees a slot. The guest cookie is the proof of "I am that guest"; the only
/// extra authorization is the blocked-pair refusal against the organizer, answered with a generic
/// code that reveals nothing about the block.
/// </summary>
public sealed class UpgradeGuestSessionUseCase(
    ISessionRepository sessions,
    ISessionLockRepository sessionLocks,
    IBlockRepository blocks,
    ISessionNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<GuestCommandResult> UpgradeAsync(
        SessionId sessionId,
        GuestLinkId guestLinkId,
        UserId userId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                // A-068 lock order (§6.1, the SessionJoinRequestUseCases convention): a non-locking
                // probe names the affected registered users, those rows and the block pair are
                // locked, and only then is the session parent locked and loaded.
                Session? probe = await sessions.FindNoTrackingAsync(sessionId, ct);
                if (probe is null)
                {
                    return GuestCommandResult.Failed("session_not_found");
                }

                UserId organizerId = probe.OrganizerId;
                await sessionLocks.LockUsersAsync([organizerId, userId], ct);

                // The blocked-pair lock is acquired here to preserve the users -> pairs -> parent
                // lock order, but the decision it feeds is deferred past the terminal/unclaimed
                // checks below so a terminal or unclaimed link never answers "upgrade_unavailable".
                IReadOnlyList<Block> pair = await blocks.LockPairsAsync(userId, [organizerId], ct);

                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return GuestCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;

                GuestLink? link = session.GuestLinks.FirstOrDefault(l => l.Id == guestLinkId);
                if (link is null)
                {
                    return GuestCommandResult.Failed("session_not_found");
                }

                // The same resolution the guest's own reads go through: a revoked link AND an ended
                // or canceled session both land on a notice, and neither can be upgraded through.
                if (session.ResolveGuestLink(guestLinkId, clock) != GuestLinkView.LiveView)
                {
                    return GuestCommandResult.Failed("guest_link_terminal");
                }

                if (link.GuestIdentityId is not { } guestIdentityId)
                {
                    return GuestCommandResult.Failed("guest_link_unclaimed");
                }

                if (pair.Count > 0)
                {
                    return GuestCommandResult.Failed("upgrade_unavailable");
                }

                session.UpgradeGuest(guestIdentityId, userId, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                return GuestCommandResult.Ok(session);
            },
            cancellationToken);
}
