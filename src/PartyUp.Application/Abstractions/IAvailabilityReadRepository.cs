using NodaTime;
using PartyUp.Application.Availability;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The availability read port (PHASE5_DESIGN.md §5.1). Both methods return raw rows only — neither
/// expands, merges, or ranks (A-076); that is Application's and Domain's job.
/// <see cref="ReadOwnersWindowAsync"/>'s statement count is fixed at four regardless of owner count,
/// which is the number the A-079 budget is stated in
/// (<c>AvailabilityReadRepositoryStatementCountTests</c>, S2).
/// </summary>
public interface IAvailabilityReadRepository
{
    Task<IReadOnlyList<AvailabilityOwnerData>> ReadOwnersWindowAsync(
        IReadOnlyCollection<UserId> ownerIds,
        TimeRange window,
        CancellationToken cancellationToken);

    /// <summary>
    /// One owner's live weekly rules, deliberately <em>not</em> window-scoped on the leading side:
    /// D-025 addresses a rule, so a series whose first occurrence is months out still has to be
    /// listable and deletable, which <see cref="ReadOwnersWindowAsync"/>'s A-076 padded fetch
    /// (bounded above by the window) can never return. Series that ended before
    /// <paramref name="notEndedBefore"/>, and split-emptied ones, are excluded — those are history,
    /// not rules the owner can still act on.
    /// </summary>
    Task<IReadOnlyList<AvailabilitySeries>> ReadOwnerSeriesAsync(
        UserId ownerId,
        Instant notEndedBefore,
        CancellationToken cancellationToken);
}
