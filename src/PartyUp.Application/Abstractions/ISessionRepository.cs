using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The Session aggregate's own persistence port (PHASE4_DESIGN.md §4.2/§5.1). Every invariant-touching
/// command loads through <see cref="LoadForUpdateAsync"/>, which locks the session parent row before
/// its participations, join requests, guest identities, guest links, mutes and applicable handoffs are
/// read — no occupancy query ever runs earlier (A-068/A-069, §6.1 step 4).
/// </summary>
public interface ISessionRepository
{
    void Add(Session session);

    /// <summary>
    /// A non-locking read for callers that only need the aggregate shape (e.g. a round-trip check);
    /// invariant-touching commands must use <see cref="LoadForUpdateAsync"/> instead.
    /// </summary>
    Task<Session?> FindAsync(SessionId id, CancellationToken cancellationToken);

    /// <summary>
    /// A non-tracking read for a pre-lock authorization probe (PHASE4_DESIGN.md §6.1 step 1): the
    /// returned aggregate is never entered into the change tracker, so a later
    /// <see cref="LoadForUpdateAsync"/> call for the same id in the same unit of work is guaranteed to
    /// see a fresh row under its own lock rather than EF's identity-map reuse of this earlier, untracked
    /// probe. Callers that will reload the same aggregate for mutation later in the same transaction
    /// must use this instead of <see cref="FindAsync"/>.
    /// </summary>
    Task<Session?> FindNoTrackingAsync(SessionId id, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the session parent with <c>FOR NO KEY UPDATE</c> (A-069) before loading every child
    /// collection the aggregate owns, plus the sibling <c>notification_mutes</c> and
    /// <c>session_reminder_handoffs</c> rows for this session (§4.2) — neither is part of the
    /// aggregate's own child collections, but both are written only under this same parent lock.
    /// This is the sole entry point invariant-touching commands use.
    /// </summary>
    Task<SessionForUpdate?> LoadForUpdateAsync(SessionId id, CancellationToken cancellationToken);
}
