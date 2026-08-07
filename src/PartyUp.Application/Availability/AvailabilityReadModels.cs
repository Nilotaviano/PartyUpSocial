using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Availability;

/// <summary>
/// One owner's raw availability rows plus their current profile zone (PHASE5_DESIGN.md §5.1). Series
/// rows are already A-076-pad-fetched — some may expand to zero occurrences inside the true window,
/// which <c>AvailabilityProjection.Project</c> resolves, never this record.
/// </summary>
public sealed record AvailabilityOwnerData(
    UserId OwnerId,
    DateTimeZone ZoneId,
    IReadOnlyList<AvailabilityBlock> Blocks,
    IReadOnlyList<AvailabilitySeries> Series,
    IReadOnlyList<AvailabilityOccurrenceSkip> Skips);
