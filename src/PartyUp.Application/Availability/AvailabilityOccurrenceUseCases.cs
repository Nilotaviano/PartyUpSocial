using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Availability;

/// <summary>The scope of an occurrence edit (§8's closed discriminated union).</summary>
public enum OccurrenceEditScope
{
    OnlyThis,
    ThisAndFuture,
}

/// <summary>
/// <see cref="EditAvailabilityOccurrenceUseCase.ExecuteAsync(UserId, AvailabilitySeriesId, LocalDate,
/// OccurrenceEditScope, TimeRange?, WeeklyPattern?, CancellationToken)"/>'s response (§8.1's
/// <c>EditOccurrenceResponse</c>): <c>onlyThis</c> populates <see cref="Block"/> only; <c>thisAndFuture</c>
/// populates <see cref="Successor"/> always and <see cref="Original"/> unless the split emptied it
/// (<c>SeriesSplitResult.OriginalBecameEmpty</c>).
/// </summary>
public sealed record OccurrenceEditOutcome(
    OccurrenceEditScope Scope,
    AvailabilityBlock? Block,
    AvailabilitySeries? Original,
    AvailabilitySeries? Successor)
{
    public static OccurrenceEditOutcome OnlyThis(AvailabilityBlock block) => new(
        OccurrenceEditScope.OnlyThis, block, null, null);

    public static OccurrenceEditOutcome ThisAndFuture(AvailabilitySeries? original, AvailabilitySeries successor) =>
        new(OccurrenceEditScope.ThisAndFuture, null, original, successor);
}

/// <summary>
/// The D-134/D-143 occurrence edit dispatch (PHASE5_DESIGN.md §5.2, §6.1). No Domain change: every
/// branch composes <see cref="AvailabilitySeriesEditor"/>'s shipped members (A-076). One transaction
/// under P5-LK-01 — both scopes take every §6.1 lock this series set touches before re-validating.
/// </summary>
public sealed class EditAvailabilityOccurrenceUseCase(IAvailabilityRepository availability, IUnitOfWork unitOfWork)
{
    public Task<OccurrenceEditOutcome> ExecuteAsync(
        UserId ownerId,
        AvailabilitySeriesId seriesId,
        LocalDate occurrenceDate,
        OccurrenceEditScope scope,
        TimeRange? onlyThisReplacement,
        WeeklyPattern? thisAndFutureReplacement,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await availability.LockOwnerAsync(ownerId, ct);

                IReadOnlyList<AvailabilitySeries> lockedSeries = await availability.LockSeriesAsync([seriesId], ct);
                AvailabilitySeries? series = lockedSeries.FirstOrDefault(s => s.Id == seriesId && s.OwnerId == ownerId);
                if (series is null)
                {
                    throw new DomainException(DomainError.NotFound, "No such availability series.");
                }

                IReadOnlyList<AvailabilityBlock> lockedBlocks =
                    await availability.LockBlocksBySourceSeriesAsync([seriesId], ct);
                IReadOnlyList<AvailabilityOccurrenceSkip> lockedSkips =
                    await availability.LockSkipsAsync([seriesId], ct);

                return scope switch
                {
                    OccurrenceEditScope.OnlyThis => ExecuteOnlyThis(
                        series, occurrenceDate, onlyThisReplacement!.Value, lockedSkips, lockedBlocks),
                    OccurrenceEditScope.ThisAndFuture => ExecuteThisAndFuture(
                        series, occurrenceDate, thisAndFutureReplacement!.Value, lockedSkips, lockedBlocks),
                    _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown occurrence edit scope."),
                };
            },
            cancellationToken);

    /// <summary>
    /// §5.2's three branches, dispatched on rows the step-4 locks already returned — no second
    /// <c>SELECT</c>.
    /// </summary>
    private OccurrenceEditOutcome ExecuteOnlyThis(
        AvailabilitySeries series,
        LocalDate occurrenceDate,
        TimeRange replacement,
        IReadOnlyList<AvailabilityOccurrenceSkip> lockedSkips,
        IReadOnlyList<AvailabilityBlock> lockedBlocks)
    {
        AvailabilityOccurrenceSkip? existingSkip = availability.FindSkip(lockedSkips, series.Id, occurrenceDate);
        if (existingSkip is null)
        {
            // No skip yet: the first edit of this date.
            OccurrenceEditResult result = AvailabilitySeriesEditor.EditOccurrence(series, occurrenceDate, replacement);
            availability.AddSkip(result.Skip);
            availability.AddBlock(result.Block);
            return OccurrenceEditOutcome.OnlyThis(result.Block);
        }

        AvailabilityBlock? existingBlock = availability.FindMaterializedBlock(lockedBlocks, series.Id, occurrenceDate);
        if (existingBlock is null)
        {
            // Skipped with no materialized block: the occurrence was deleted (delete-one, or a delete
            // of the block a prior edit produced). There is nothing left to edit.
            throw new DomainException(DomainError.NotFound, "No such availability occurrence.");
        }

        // Re-edit: reschedule the existing artifact in place — no second skip, same blockId.
        existingBlock.Reschedule(replacement);
        return OccurrenceEditOutcome.OnlyThis(existingBlock);
    }

    private OccurrenceEditOutcome ExecuteThisAndFuture(
        AvailabilitySeries series,
        LocalDate occurrenceDate,
        WeeklyPattern replacement,
        IReadOnlyList<AvailabilityOccurrenceSkip> lockedSkips,
        IReadOnlyList<AvailabilityBlock> lockedBlocks)
    {
        SeriesSplitResult split = AvailabilitySeriesEditor.SplitFromOccurrence(
            series, occurrenceDate, replacement, lockedSkips, lockedBlocks);

        // §12-20: every skip and materialized block dated on or after the split is dropped, never
        // migrated — over the rows the step-4 locks already returned.
        availability.RemoveSkipsFrom(series.Id, split.SplitDate);
        availability.RemoveMaterializedBlocksFrom(series.Id, split.SplitDate);
        availability.AddSeries(split.Successor);

        if (split.OriginalBecameEmpty)
        {
            availability.RemoveSeries(split.Original);
            return OccurrenceEditOutcome.ThisAndFuture(null, split.Successor);
        }

        return OccurrenceEditOutcome.ThisAndFuture(split.Original, split.Successor);
    }
}

/// <summary>
/// Delete-one (§8): idempotent via the skip PK. On a date already edited via <c>onlyThis</c>, also
/// removes that edit's materialized block — the skip write stays idempotent, but the block it produced
/// is not otherwise reachable for cleanup.
/// </summary>
public sealed class DeleteAvailabilityOccurrenceUseCase(IAvailabilityRepository availability, IUnitOfWork unitOfWork)
{
    public Task<bool> ExecuteAsync(
        UserId ownerId,
        AvailabilitySeriesId seriesId,
        LocalDate occurrenceDate,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await availability.LockOwnerAsync(ownerId, ct);

                IReadOnlyList<AvailabilitySeries> lockedSeries = await availability.LockSeriesAsync([seriesId], ct);
                AvailabilitySeries? series = lockedSeries.FirstOrDefault(s => s.Id == seriesId && s.OwnerId == ownerId);
                if (series is null)
                {
                    throw new DomainException(DomainError.NotFound, "No such availability series.");
                }

                IReadOnlyList<AvailabilityBlock> lockedBlocks =
                    await availability.LockBlocksBySourceSeriesAsync([seriesId], ct);
                IReadOnlyList<AvailabilityOccurrenceSkip> lockedSkips =
                    await availability.LockSkipsAsync([seriesId], ct);

                AvailabilityOccurrenceSkip? existingSkip = availability.FindSkip(lockedSkips, seriesId, occurrenceDate);
                if (existingSkip is null)
                {
                    AvailabilityOccurrenceSkip skip = AvailabilitySeriesEditor.DeleteOccurrence(series, occurrenceDate);
                    availability.AddSkip(skip);
                    return true;
                }

                // Already skipped: idempotent no-op on the skip itself, but a prior onlyThis edit on
                // this date left a materialized block behind — remove it too (§8).
                AvailabilityBlock? existingBlock =
                    availability.FindMaterializedBlock(lockedBlocks, seriesId, occurrenceDate);
                if (existingBlock is not null)
                {
                    availability.RemoveBlock(existingBlock);
                }

                return true;
            },
            cancellationToken);
}
