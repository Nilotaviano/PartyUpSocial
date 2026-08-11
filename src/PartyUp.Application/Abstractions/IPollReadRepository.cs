using PartyUp.Application.Availability;
using PartyUp.Application.Polls;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The poll surface's read-side lookups beyond the aggregate itself: bulk display-name resolution
/// (PHASE6_DESIGN.md §4 "GET /{id}") mirroring <c>SessionReadRepository.FetchDisplayNamesAsync</c>,
/// and the two paginated list bands plus the calendar layer S4c adds (§4.1, §5 calendar bullet) --
/// each mirroring <c>ISessionReadRepository</c>'s own list/calendar shape over
/// <see cref="Infrastructure.Persistence.Repositories.PollVisibilitySql"/> instead of
/// <c>SessionVisibilitySql</c>.
/// </summary>
public interface IPollReadRepository
{
    /// <summary>
    /// Bulk name+avatar resolution for a poll's registered participants. One prefetch, both fields:
    /// the avatar rides the display name so no read path can resolve one without the other.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, UserNameplate>> FindNameplatesAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken);

    /// <summary>§7 rule 1: the organizer or an Active participant, status in {Polling, Closed} (§4.1's D-160 note).</summary>
    Task<SessionPage<PollSummary>> ListInvolvedAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>§7 rule 2: non-private, still-<c>Polling</c>, reached only through inherited access (D-118).</summary>
    Task<SessionPage<PollSummary>> ListDiscoverableAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// PHASE6_DESIGN.md §5 calendar bullet: the calendar's <c>pollOptions</c> layer -- rule-1
    /// involvement only (calendar is personal), status in {Polling, Closed}, options overlapping
    /// <paramref name="range"/> the same way <c>ListCalendarSessionsAsync</c> overlaps sessions.
    /// </summary>
    Task<IReadOnlyList<CalendarPollOption>> ListCalendarPollOptionsAsync(
        UserId viewerId,
        TimeRange range,
        CancellationToken cancellationToken);
}
