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

/// <summary>
/// PHASE5_DESIGN.md §5.1/§8.1: the authoring identity of one <see cref="MyAvailabilityOccurrence"/> —
/// <c>blockId</c> xor <c>(seriesId, occurrenceDate)</c> (A-080), because <c>DOMAIN_MODEL.md:198</c>
/// requires an edit to address a concrete block or (series, date).
/// </summary>
public abstract record AvailabilityOccurrenceOrigin
{
    private AvailabilityOccurrenceOrigin()
    {
    }

    public sealed record FromBlock(AvailabilityBlockId BlockId) : AvailabilityOccurrenceOrigin;

    public sealed record FromSeriesOccurrence(AvailabilitySeriesId SeriesId, LocalDate OccurrenceDate) : AvailabilityOccurrenceOrigin;
}

/// <summary>One authored occurrence in the owner's own zone — the edit surface (hit-testing, the scope dialog, delete).</summary>
public sealed record MyAvailabilityOccurrence(TimeRange Range, AvailabilityOccurrenceOrigin Origin);

/// <summary>
/// PHASE5_DESIGN.md §5.1: <c>myAvailability</c> carries both forms D-101 requires — the addressable
/// <see cref="Occurrences"/> (the edit surface) and the bare <see cref="Merged"/> ranges the week grid
/// paints (D-101's visual half) and that feed <c>OverlapEngine</c> for a friend owner (the algorithmic
/// half). <see cref="Range"/> is the echoed request range. <see cref="Series"/> carries the owner's
/// recurring rules themselves — D-025 addresses a rule, not a mark, so unlike every other member it
/// is not scoped to <see cref="Range"/>: a series is listed whether its occurrences here are all
/// skipped or its first occurrence has not arrived yet.
/// </summary>
public sealed record MyAvailabilityLayer(
    IReadOnlyList<MyAvailabilityOccurrence> Occurrences,
    IReadOnlyList<TimeRange> Merged,
    TimeRange Range,
    IReadOnlyList<AvailabilitySeries> Series);

/// <summary>
/// PHASE5_DESIGN.md A-080: one calendar <c>friendAvailability</c> row — ranges and the owner's id only
/// (never a block/series id, which the viewer could never address anyway; masking to a
/// <c>MaskedParticipant</c> is S5's wire concern).
/// </summary>
public sealed record FriendAvailabilityEntry(UserId OwnerId, IReadOnlyList<TimeRange> Ranges);
