using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Application.Availability;

/// <summary>
/// One candidate instant on an involved poll, with the viewer's own current vote (PHASE6_DESIGN.md
/// §5 calendar bullet) -- null when the viewer has not voted on this option yet.
/// </summary>
public sealed record CalendarPollOptionInstant(PollOptionId OptionId, Instant StartTime, VoteChoice? OwnVote);

/// <summary>
/// A-080's poll layer, filled by S4c (§1.2 declared it empty in Phase 5): one row per poll the viewer
/// is involved in (§7 rule 1 only -- the calendar is personal, not a discovery surface) whose options
/// overlap the requested range.
/// </summary>
public sealed record CalendarPollOption(
    PollId PollId,
    SessionTitle Title,
    IReadOnlyList<CalendarPollOptionInstant> Options);

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
/// <c>any</c>, wider than Discover (A-077 (d)). <c>pollOptions</c> (S4c) is
/// <see cref="Abstractions.IPollReadRepository.ListCalendarPollOptionsAsync"/>, narrower than
/// <c>sessions</c> -- rule 1 only, since a poll a viewer merely could join has no seat to show yet.
/// </summary>
public sealed class GetCalendarViewUseCase(
    ReadMyAvailabilityUseCase myAvailability,
    IFriendshipRepository friendships,
    IAvailabilityReadRepository availabilityReads,
    ISessionReadRepository sessionReads,
    IPollReadRepository pollReads,
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

        IReadOnlyList<CalendarPollOption> pollOptions =
            await pollReads.ListCalendarPollOptionsAsync(viewerId, range, cancellationToken);

        return new CalendarView(mine, friendAvailability, sessions, pollOptions);
    }
}
