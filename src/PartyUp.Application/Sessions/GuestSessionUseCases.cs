using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Sessions;

/// <summary>
/// A guest bearer's read and write surface (PHASE4_DESIGN.md §5.2). Guest claim, rename, and response
/// share the same parent-lock/transaction path as registered mutations (§6.1): none of them introduces
/// a new registered counterpart, so only the session parent is locked before occupancy is read.
/// Guests are never notification recipients (D-167) and never hold a reminder handoff, so no
/// <see cref="ISessionReminderHandoffWriter"/> call is made for the guest actor themselves — only the
/// materializer, for whatever derived Session events a guest's own command raises for others (e.g. a
/// confirmation crossing, or an organizer's waitlist-vacancy notice). Guest last-write-wins (D-126)
/// means there is no client version/conflict check here, never an absence of the parent lock.
/// The HTTP bearer-token → (SessionId, GuestLinkId) exchange is S4's <c>IGuestLinkResolver</c>; every
/// method below already takes the resolved pair.
/// </summary>
public sealed class GuestSessionUseCases(
    ISessionRepository sessions,
    ISessionReadRepository reads,
    ISessionLockRepository sessionLocks,
    ISessionNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    /// <summary>
    /// The A-064 exchange's first render: a non-locking read of what the bearer currently sees —
    /// the live view (claimed or not) or the constrained terminal notice (§5.4). A missing session or
    /// guest link is treated as "not found"; link resolution is deliberately not the fail-closed
    /// visibility engine, so this is the only 404 case here.
    /// </summary>
    public Task<GuestSessionResult> ResolveGuestLinkAsync(
        SessionId sessionId,
        GuestLinkId guestLinkId,
        CancellationToken cancellationToken) =>
        ResolveAsync(sessionId, guestLinkId, cancellationToken);

    /// <summary>
    /// The same resolution as <see cref="ResolveGuestLinkAsync"/>, used for a return visit under an
    /// already-established guest bearer (the cookie/session-carrying half of A-064 is S4's).
    /// </summary>
    public Task<GuestSessionResult> GetGuestSessionAsync(
        SessionId sessionId,
        GuestLinkId guestLinkId,
        CancellationToken cancellationToken) =>
        ResolveAsync(sessionId, guestLinkId, cancellationToken);

    /// <summary>
    /// D-017: the first visit supplies a display name and a response, closing the same as any
    /// invitation response (D-081). Unclaimed links reserve no capacity, so a guest claiming at
    /// capacity waitlists like anyone (D-084, §12-6).
    /// </summary>
    public Task<GuestCommandResult> ClaimGuestAsync(
        SessionId sessionId,
        GuestLinkId guestLinkId,
        DisplayName displayName,
        ParticipationStatus response,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return GuestCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                GuestIdentity identity = session.ClaimGuestLink(guestLinkId, displayName, response, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                return GuestCommandResult.Ok(session, identity.Id);
            },
            cancellationToken);

    /// <summary>D-044/D-126: renaming through the same link — last write wins, no conflict check.</summary>
    public Task<GuestCommandResult> RenameGuestAsync(
        SessionId sessionId,
        GuestLinkId guestLinkId,
        DisplayName displayName,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return GuestCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                session.RenameGuest(guestLinkId, displayName, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                return GuestCommandResult.Ok(session);
            },
            cancellationToken);

    /// <summary>
    /// D-126: multi-device guest responses are last-write-wins — the parent lock is what makes the
    /// capacity/confirmation re-evaluation race-free, not a version check on the guest's own row.
    /// </summary>
    public Task<GuestCommandResult> RespondAsGuestAsync(
        SessionId sessionId,
        GuestLinkId guestLinkId,
        ParticipationStatus response,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await sessionLocks.LockSessionsAsync([sessionId], ct);

                SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, ct);
                if (forUpdate is null)
                {
                    return GuestCommandResult.Failed("session_not_found");
                }

                Session session = forUpdate.Session;
                session.RespondAsGuest(guestLinkId, response, clock);

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                return GuestCommandResult.Ok(session);
            },
            cancellationToken);

    private async Task<GuestSessionResult> ResolveAsync(
        SessionId sessionId,
        GuestLinkId guestLinkId,
        CancellationToken cancellationToken)
    {
        Session? session = await sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return GuestSessionResult.NotFound;
        }

        GuestLinkView view;
        try
        {
            view = session.ResolveGuestLink(guestLinkId, clock);
        }
        catch (DomainException ex) when (ex.Error == DomainError.NotFound)
        {
            return GuestSessionResult.NotFound;
        }

        if (view != GuestLinkView.LiveView)
        {
            return GuestSessionResult.Terminal(view);
        }

        GuestSessionView? live = await reads.FindGuestViewAsync(guestLinkId, cancellationToken);
        return live is null ? GuestSessionResult.NotFound : GuestSessionResult.Ok(live);
    }
}

/// <summary>
/// The result of a guest mutation: either the mutated aggregate (with the claimed guest's identity id,
/// for <see cref="GuestSessionUseCases.ClaimGuestAsync"/>) or a failure code.
/// </summary>
public sealed record GuestCommandResult(bool Succeeded, string? Code, Session? Session, GuestIdentityId? GuestIdentityId)
{
    public static GuestCommandResult Ok(Session session, GuestIdentityId? guestIdentityId = null) =>
        new(true, null, session, guestIdentityId);

    public static GuestCommandResult Failed(string code) => new(false, code, null, null);
}

/// <summary>
/// What a guest-link resolution renders: the constrained A-074 terminal notice, or the live view
/// (§5.4). Not found is its own case — a missing session or link, distinct from a found-but-terminal
/// link — because link resolution is not the fail-closed visibility engine and never returns a 404
/// shape indistinguishable from "revoked"/"canceled"/"ended".
/// </summary>
public sealed record GuestSessionResult(bool Found, GuestLinkView View, GuestSessionView? Live)
{
    public static readonly GuestSessionResult NotFound = new(false, default, null);

    public static GuestSessionResult Terminal(GuestLinkView view) => new(true, view, null);

    public static GuestSessionResult Ok(GuestSessionView live) => new(true, GuestLinkView.LiveView, live);
}
