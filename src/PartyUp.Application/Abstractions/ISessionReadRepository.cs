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

    /// <summary>Same viewer-scoping contract as <see cref="ListUpcomingAsync"/>, over Completed history.</summary>
    Task<SessionPage<SessionSummary>> ListHistoryAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<SessionPage<JoinRequestSummary>> ListJoinRequestsAsync(
        SessionId sessionId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<GuestSessionView?> FindGuestViewAsync(GuestLinkId guestLinkId, CancellationToken cancellationToken);
}
