using NodaTime;
using PartyUp.Application.Polls;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The Session read port (PHASE4_DESIGN.md §5.1). Every method is a plain, non-locking projection —
/// visibility and identity masking are applied by the caller before a result reaches an endpoint, not
/// performed here. Page size and keyset ordering follow A-072: <c>upcoming</c>/<c>history</c> sort on
/// <c>start_time</c>, join requests sort on <c>created_at</c>, both tie-broken by <c>id</c>.
/// </summary>
public interface ISessionReadRepository
{
    Task<SessionDetail?> FindDetailAsync(SessionId sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// A-072's <c>upcoming</c> keyset, scoped to what <paramref name="viewerId"/> may see (§7): the
    /// predicate is applied in SQL rather than after paging, so a page never shrinks below
    /// <paramref name="pageSize"/> because invisible rows were filtered out afterward (carried S1
    /// verifier note — PHASE4_DESIGN.md §5.1).
    /// </summary>
    Task<SessionPage<SessionSummary>> ListUpcomingAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// D-221: the same keyset and viewer-scoping contract as <see cref="ListUpcomingAsync"/>, over the
    /// sessions the viewer actually took part in — organizer, or an active (non-waitlisted)
    /// <c>Going</c> participation — with materialized status <c>Completed</c> or <c>Canceled</c>. The
    /// A-077 visibility fragment is applied on top of that attachment test rather than instead of it,
    /// so the surface still fails closed if the two ever disagree. Each summary carries its
    /// <see cref="SessionSummary.Players"/> roster, unmasked — the caller decides who may be read.
    /// </summary>
    Task<SessionPage<SessionSummary>> ListHistoryAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// D-222: the whole-history recap's inputs — every fact row is one session from
    /// <see cref="ListHistoryAsync"/>'s <c>Completed</c> arm alone (a Canceled session was never
    /// played, so it counts toward nothing). Unpaged by design: the recap summarises everything, and
    /// each row is a handful of scalars plus its companion ids.
    /// </summary>
    Task<IReadOnlyList<CompletedSessionFact>> ListCompletedHistoryFactsAsync(
        UserId viewerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The display name and avatar of each requested user, for the handful of people a computed read
    /// (D-222's top mates) names after the fact. Mirrors <c>IPollReadRepository.FindNameplatesAsync</c>;
    /// a tombstoned user yields a null <c>DisplayName</c> (D-092), and a missing id yields no entry.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, UserNameplate>> FindNameplatesAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken);

    Task<SessionPage<JoinRequestSummary>> ListJoinRequestsAsync(
        SessionId sessionId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<GuestSessionView?> FindGuestViewAsync(GuestLinkId guestLinkId, CancellationToken cancellationToken);

    /// <summary>
    /// PHASE5_DESIGN.md A-077 (c)/(f): the Discover surface — <c>SessionVisibilitySql</c>'s
    /// <c>inheritedOnly</c> access-kind plus the already-attached exclusion set (organizer, any
    /// participation row whatever its status, or a Pending join request), never time-derived, ordered
    /// <c>start_time</c> ascending then <c>id</c> ascending. Cursor scope validation is S5's — this
    /// port takes only the ordering and page size (§5.1).
    /// </summary>
    Task<SessionPage<SessionSummary>> ListDiscoverAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// PHASE5_DESIGN.md A-080: the calendar <c>sessions</c> layer — <c>SessionVisibilitySql</c> at
    /// access-kind <c>any</c> plus the intersection predicate
    /// <c>start_time &lt; to AND start_time + duration &gt; from</c>. <c>Canceled</c>/<c>Expired</c>
    /// are excluded; <c>Completed</c> is included (D-056). Unpaginated (A-079).
    /// </summary>
    Task<IReadOnlyList<SessionSummary>> ListCalendarSessionsAsync(
        UserId viewerId,
        TimeRange range,
        CancellationToken cancellationToken);

    /// <summary>
    /// PHASE5_DESIGN.md §5.1/P5-HOME-01: D-103's Home region 1. Both lists are scoped to
    /// <paramref name="now"/>-future sessions with materialized status ∈ {Unconfirmed, Confirmed},
    /// through the A-077 fragment at access-kind <c>any</c>, ordered by start ascending and capped at
    /// <paramref name="cap"/> each with a <c>hasMore</c> flag.
    /// </summary>
    Task<ActionableInbox> ListActionableAsync(
        UserId viewerId,
        Instant now,
        int cap,
        CancellationToken cancellationToken);
}
