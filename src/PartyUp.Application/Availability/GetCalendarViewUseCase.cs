using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Availability;

/// <summary>
/// A-080: Phase 6's poll layer, declared and always empty so Phase 6 can add data without a contract
/// change (§1.2). Carries no members yet — Phase 6 defines its shape.
/// </summary>
public sealed record CalendarPollOption;

/// <summary>PHASE5_DESIGN.md A-080: {myAvailability, friendAvailability, sessions, pollOptions} for one range, atomically.</summary>
public sealed record CalendarView(
    MyAvailabilityLayer MyAvailability,
    IReadOnlyList<FriendAvailabilityEntry> FriendAvailability,
    IReadOnlyList<SessionSummary> Sessions,
    IReadOnlyList<CalendarPollOption> PollOptions);

/// <summary>
/// PHASE5_DESIGN.md A-080: one range-scoped, atomic read of every calendar layer — four round trips
/// would let the layers disagree mid-render. <c>myAvailability</c> is never re-derived
/// (<see cref="ReadMyAvailabilityUseCase"/> is called, not re-implemented); the viewer's own blocks
/// never reach <c>OverlapEngine</c> here (§12-44 — this use case runs no overlap search at all).
/// <c>friendAvailability</c> carries ranges and an owner id only (no block/series id, A-080). The
/// <c>sessions</c> layer is <see cref="ISessionReadRepository.ListCalendarSessionsAsync"/>, access-kind
/// <c>any</c>, wider than Discover (A-077 (d)). <c>pollOptions</c> is declared and always empty.
/// </summary>
public sealed class GetCalendarViewUseCase(
    ReadMyAvailabilityUseCase myAvailability,
    IFriendshipRepository friendships,
    IAvailabilityReadRepository availabilityReads,
    ISessionReadRepository sessionReads,
    IDateTimeZoneProvider zoneProvider)
{
    public async Task<CalendarView> ExecuteAsync(UserId viewerId, TimeRange range, CancellationToken cancellationToken)
    {
        OverlapEngine.EnsureRangeAllowed(range);

        MyAvailabilityLayer mine = await myAvailability.ExecuteAsync(viewerId, range, cancellationToken);

        IReadOnlyList<AcceptedFriendProjection> accepted =
            await friendships.ListAcceptedAsync(viewerId, cancellationToken);
        List<UserId> friendIds = [.. accepted.Select(f => f.Counterpart.Id)];

        IReadOnlyList<AvailabilityOwnerData> ownerData =
            await availabilityReads.ReadOwnersWindowAsync(friendIds, range, cancellationToken);

        List<FriendAvailabilityEntry> friendAvailability = [];
        foreach (AvailabilityOwnerData data in ownerData)
        {
            IReadOnlyList<TimeRange> projected = AvailabilityProjection.Project(
                data.Blocks, data.Series, data.Skips, range, zoneProvider, data.ZoneId);
            friendAvailability.Add(new FriendAvailabilityEntry(data.OwnerId, projected));
        }

        IReadOnlyList<SessionSummary> sessions =
            await sessionReads.ListCalendarSessionsAsync(viewerId, range, cancellationToken);

        return new CalendarView(mine, friendAvailability, sessions, []);
    }
}
