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
    /// Locks the session parent with <c>FOR NO KEY UPDATE</c> (A-069) before loading every child
    /// collection the aggregate owns, plus the sibling <c>notification_mutes</c> and
    /// <c>session_reminder_handoffs</c> rows for this session (§4.2) — neither is part of the
    /// aggregate's own child collections, but both are written only under this same parent lock.
    /// This is the sole entry point invariant-touching commands use.
    /// </summary>
    Task<SessionForUpdate?> LoadForUpdateAsync(SessionId id, CancellationToken cancellationToken);
}
