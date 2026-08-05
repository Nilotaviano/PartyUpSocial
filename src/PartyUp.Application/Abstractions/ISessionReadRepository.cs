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

    Task<SessionPage<SessionSummary>> ListUpcomingAsync(
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<SessionPage<SessionSummary>> ListHistoryAsync(
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
