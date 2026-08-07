using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Availability;

/// <summary>
/// Weekly series commands (PHASE5_DESIGN.md §5.2, §6.1, D-025/D-133). A plain create touches only
/// <c>users</c> — no existing series, block or skip row is read or locked.
/// </summary>
public sealed class CreateAvailabilitySeriesUseCase(IAvailabilityRepository availability, IUnitOfWork unitOfWork)
{
    public Task<AvailabilitySeries> ExecuteAsync(
        UserId ownerId,
        WeeklyPattern pattern,
        DateTimeZone zone,
        LocalDate effectiveFrom,
        LocalDate? effectiveUntil,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                AvailabilityVisibility.RequireWrite(ownerId, Viewer.Create(ownerId));
                await availability.LockOwnerAsync(ownerId, ct);

                AvailabilitySeries series = AvailabilitySeries.Create(
                    ownerId, pattern, zone, effectiveFrom, effectiveUntil);
                availability.AddSeries(series);
                return series;
            },
            cancellationToken);
}

/// <summary>
/// Deletes a whole series (D-025), cascading to its skip exceptions and edit-one materialized blocks
/// (§12-21). Idempotent: a missing or non-owner target is a no-op, matching the shipped delete
/// convention (§8).
/// </summary>
public sealed class DeleteAvailabilitySeriesUseCase(IAvailabilityRepository availability, IUnitOfWork unitOfWork)
{
    public Task<bool> ExecuteAsync(
        UserId ownerId, AvailabilitySeriesId seriesId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                AvailabilityVisibility.RequireWrite(ownerId, Viewer.Create(ownerId));
                await availability.LockOwnerAsync(ownerId, ct);

                IReadOnlyList<AvailabilitySeries> lockedSeries = await availability.LockSeriesAsync([seriesId], ct);
                AvailabilitySeries? series = lockedSeries.FirstOrDefault(s => s.Id == seriesId && s.OwnerId == ownerId);
                if (series is null)
                {
                    return false;
                }

                IReadOnlyList<AvailabilityBlock> lockedBlocks =
                    await availability.LockBlocksBySourceSeriesAsync([seriesId], ct);
                IReadOnlyList<AvailabilityOccurrenceSkip> lockedSkips =
                    await availability.LockSkipsAsync([seriesId], ct);

                SeriesDeletionResult deletion =
                    AvailabilitySeriesEditor.DeleteSeries(series, lockedSkips, lockedBlocks);

                availability.RemoveSkipsFrom(seriesId, series.EffectiveFrom);
                availability.RemoveMaterializedBlocksFrom(seriesId, series.EffectiveFrom);
                availability.RemoveSeries(deletion.Series);
                return true;
            },
            cancellationToken);
}
