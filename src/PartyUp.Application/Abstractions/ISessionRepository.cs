using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The Session aggregate's own persistence port (PHASE4_DESIGN.md §4.2/§5.1). Every invariant-touching
/// command loads through <see cref="LoadForUpdateAsync"/>, which locks the session parent row before
/// its participations, join requests, guest identities and guest links are read — no occupancy query
/// ever runs earlier (A-068/A-069, §6.1 step 4).
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
    /// collection the aggregate owns. This is the sole entry point invariant-touching commands use.
    /// </summary>
    Task<Session?> LoadForUpdateAsync(SessionId id, CancellationToken cancellationToken);
}
