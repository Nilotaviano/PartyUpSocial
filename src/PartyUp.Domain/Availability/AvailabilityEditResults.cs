using NodaTime;

namespace PartyUp.Domain.Availability;

/// <summary>Edit-one (D-143): the occurrence is skipped and replaced by a concrete block.</summary>
public sealed record OccurrenceEditResult(AvailabilityOccurrenceSkip Skip, AvailabilityBlock Block);

/// <summary>
/// Edit-this-and-future (D-143/§12-20): the original is clipped at the selected occurrence's date,
/// a successor takes over from that same date, and skip exceptions plus materialized blocks dated on
/// or after the split are dropped rather than migrated.
/// </summary>
public sealed record SeriesSplitResult(
    AvailabilitySeries Original,
    AvailabilitySeries Successor,
    LocalDate SplitDate,
    IReadOnlyList<AvailabilityOccurrenceSkip> DroppedSkips,
    IReadOnlyList<AvailabilityBlock> DroppedBlocks)
{
    /// <summary>True when the split consumed the whole original — the caller deletes the row.</summary>
    public bool OriginalBecameEmpty => Original.IsEmpty;
}

/// <summary>§12-21: deleting a series cascades to its skip rows and its materialized blocks.</summary>
public sealed record SeriesDeletionResult(
    AvailabilitySeries Series,
    IReadOnlyList<AvailabilityOccurrenceSkip> CascadedSkips,
    IReadOnlyList<AvailabilityBlock> CascadedBlocks);
