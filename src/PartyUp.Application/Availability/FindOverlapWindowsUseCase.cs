using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Availability;

/// <summary>A-078: whether the caller named friends explicitly or the search fell back to all of them (D-110).</summary>
public enum SelectionSource
{
    Explicit,
    AllFriends,
}

/// <summary>
/// PHASE5_DESIGN.md §8/§8.1's response shape, at the Application layer: no <c>resolvedFriendIds</c>,
/// ever (A-078) — <see cref="ConsideredFriendIds"/> is present (non-null) iff
/// <see cref="SelectionSource"/> is <see cref="Availability.SelectionSource.AllFriends"/>. Ordinal
/// assignment and display-name resolution into the wire <c>MaskedParticipant</c> shape are S5's.
/// </summary>
public sealed record OverlapSearchResult(
    IReadOnlyList<OverlapWindow> Windows,
    TimeRange Range,
    SelectionSource SelectionSource,
    IReadOnlyList<UserId>? ConsideredFriendIds);

/// <summary>
/// PHASE5_DESIGN.md §5.2: selection resolution (D-110 — omitted <paramref name="friendIds"/> means
/// every accepted friend), filtered through <see cref="AvailabilityVisibility.ReadableOwners"/>
/// (A-078/§12-45 — a selection the viewer may not read is dropped silently, never echoed), the range
/// defaulted to <see cref="OverlapEngine.DefaultRange"/> and validated by
/// <see cref="OverlapEngine.EnsureRangeAllowed"/>, each owner projected in their own zone, then
/// <see cref="OverlapEngine.FindWindows"/> called exactly once. A-079's budget: exactly one
/// <see cref="IFriendshipRepository.ListAcceptedAsync"/> call (the friend-set call, needed for both
/// selection sources to resolve <see cref="AvailabilityVisibility.ReadableOwners"/> — blocking already
/// deletes the friendship, D-065, so no separate block read is needed) and exactly one
/// <see cref="IAvailabilityReadRepository.ReadOwnersWindowAsync"/> call, regardless of selection size.
/// </summary>
public sealed class FindOverlapWindowsUseCase(
    IFriendshipRepository friendships,
    IAvailabilityReadRepository availabilityReads,
    IDateTimeZoneProvider zoneProvider,
    IClock clock)
{
    public async Task<OverlapSearchResult> ExecuteAsync(
        UserId viewerId,
        IReadOnlyCollection<UserId>? friendIds,
        TimeRange? range,
        CancellationToken cancellationToken)
    {
        TimeRange effectiveRange = range ?? OverlapEngine.DefaultRange(clock);
        OverlapEngine.EnsureRangeAllowed(effectiveRange);

        IReadOnlyList<AcceptedFriendProjection> accepted =
            await friendships.ListAcceptedAsync(viewerId, cancellationToken);
        List<UserId> acceptedFriendIds = [.. accepted.Select(f => f.Counterpart.Id)];
        Viewer viewer = Viewer.Create(viewerId, acceptedFriendIds);

        SelectionSource selectionSource = friendIds is null ? SelectionSource.AllFriends : SelectionSource.Explicit;
        IReadOnlyCollection<UserId> requested = friendIds ?? acceptedFriendIds;
        IReadOnlyList<UserId> readableOwners = AvailabilityVisibility.ReadableOwners(requested, viewer);

        IReadOnlyList<AvailabilityOwnerData> ownerData =
            await availabilityReads.ReadOwnersWindowAsync(readableOwners, effectiveRange, cancellationToken);

        List<FriendAvailability> friendAvailability = [];
        foreach (AvailabilityOwnerData data in ownerData)
        {
            IReadOnlyList<TimeRange> projected = AvailabilityProjection.Project(
                data.Blocks, data.Series, data.Skips, effectiveRange, zoneProvider, data.ZoneId);
            friendAvailability.Add(FriendAvailability.Create(data.OwnerId, projected));
        }

        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(friendAvailability, effectiveRange);

        return new OverlapSearchResult(
            windows,
            effectiveRange,
            selectionSource,
            selectionSource == SelectionSource.AllFriends ? readableOwners : null);
    }
}
