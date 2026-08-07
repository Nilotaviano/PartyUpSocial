using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The availability write port (PHASE5_DESIGN.md §5.1). Every method that issues SQL maps to exactly
/// one statement; locking and loading are one call, so a caller cannot read a row it did not lock.
/// <see cref="FindSkip"/> and <see cref="FindMaterializedBlock"/> issue no statement at all — pure
/// in-memory lookups over the collections a <c>Lock…</c> call already returned. Signatures here are
/// S2's first draft: S3 finalizes the locking statements against <c>P5-LK-01</c>.
/// </summary>
public interface IAvailabilityRepository
{
    /// <summary>§6.1 step 2 — the write's serialization point (A-075, P5-LK-01).</summary>
    Task LockOwnerAsync(UserId ownerId, CancellationToken cancellationToken);

    /// <summary>§6.1 step 3, id form — a write whose series set is supplied (block/occurrence edits).</summary>
    Task<IReadOnlyList<AvailabilitySeries>> LockSeriesAsync(
        IReadOnlyCollection<AvailabilitySeriesId> seriesIds, CancellationToken cancellationToken);

    /// <summary>§6.1 step 3, derived form — A-081's zone migration and the deletion executor.</summary>
    Task<IReadOnlyList<AvailabilitySeries>> LockOwnerSeriesAsync(UserId ownerId, CancellationToken cancellationToken);

    /// <summary>§6.1 step 4a, id form (block edit/delete).</summary>
    Task<IReadOnlyList<AvailabilityBlock>> LockBlocksAsync(
        IReadOnlyCollection<AvailabilityBlockId> blockIds, CancellationToken cancellationToken);

    /// <summary>§6.1 step 4a, provenance form (series delete, thisAndFuture split).</summary>
    Task<IReadOnlyList<AvailabilityBlock>> LockBlocksBySourceSeriesAsync(
        IReadOnlyCollection<AvailabilitySeriesId> seriesIds, CancellationToken cancellationToken);

    /// <summary>§6.1 step 4b — A-075's addition to A-068's declared lock order.</summary>
    Task<IReadOnlyList<AvailabilityOccurrenceSkip>> LockSkipsAsync(
        IReadOnlyCollection<AvailabilitySeriesId> seriesIds, CancellationToken cancellationToken);

    /// <summary>
    /// No statement — an in-memory lookup over the rows <see cref="LockSkipsAsync"/> already
    /// returned, serving §5.2's repeated-<c>onlyThis</c> detection.
    /// </summary>
    AvailabilityOccurrenceSkip? FindSkip(
        IReadOnlyList<AvailabilityOccurrenceSkip> lockedSkips, AvailabilitySeriesId seriesId, LocalDate occurrenceDate);

    /// <summary>
    /// No statement — the other half of that detection, over the rows
    /// <see cref="LockBlocksBySourceSeriesAsync"/> already returned.
    /// </summary>
    AvailabilityBlock? FindMaterializedBlock(
        IReadOnlyList<AvailabilityBlock> lockedBlocks, AvailabilitySeriesId seriesId, LocalDate occurrenceDate);

    void AddBlock(AvailabilityBlock block);

    void RemoveBlock(AvailabilityBlock block);

    void AddSeries(AvailabilitySeries series);

    void RemoveSeries(AvailabilitySeries series);

    void AddSkip(AvailabilityOccurrenceSkip skip);

    /// <summary>§5.2/§12-20 — drops every skip of the series dated on or after the split.</summary>
    void RemoveSkipsFrom(AvailabilitySeriesId seriesId, LocalDate from);

    /// <summary>§12-20's other half — drops materialized blocks provenanced at or after the split.</summary>
    void RemoveMaterializedBlocksFrom(AvailabilitySeriesId seriesId, LocalDate from);

    /// <summary>A-081's caller, after <see cref="LockOwnerSeriesAsync"/> — one UPDATE, zone id only.</summary>
    Task RetargetSeriesZoneAsync(UserId ownerId, string zoneId, CancellationToken cancellationToken);
}
