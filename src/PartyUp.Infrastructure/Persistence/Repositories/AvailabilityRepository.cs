using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The availability write adapter (PHASE5_DESIGN.md §5.1, A-075). Locking and loading are one call
/// per method, and each is exactly <strong>one</strong> SQL statement: every multi-row <c>Lock…</c>
/// member below issues its <c>SELECT … FOR NO KEY UPDATE</c> carrying A-075's declared <c>ORDER BY</c>
/// as a single <c>FromSqlInterpolated</c> query mapped straight onto the tracked entity — the same
/// idiom <c>BlockRepository.FindByIdForUpdateAsync</c>/<c>LockPairsAsync</c> already use — so a caller
/// can never read a row via a second, unlocked <c>SELECT</c> against a table this class already
/// locked (S3 finalization of the S2 draft, closing round-1 finding C1: the S2 draft issued a locking
/// <c>SELECT 1</c> then a separate EF load per table, which is two statements under READ COMMITTED).
/// </summary>
public sealed class AvailabilityRepository(AppDbContext context) : IAvailabilityRepository
{
    public async Task LockOwnerAsync(UserId ownerId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM users
            WHERE id = {ownerId.Value}
            FOR NO KEY UPDATE
            """, cancellationToken);

    public async Task<IReadOnlyList<AvailabilitySeries>> LockSeriesAsync(
        IReadOnlyCollection<AvailabilitySeriesId> seriesIds, CancellationToken cancellationToken)
    {
        if (seriesIds.Count == 0)
        {
            return [];
        }

        Guid[] ids = [.. seriesIds.Select(id => id.Value)];
        return await context.AvailabilitySeries
            .FromSqlInterpolated($"""
                SELECT *
                FROM availability_series
                WHERE id = ANY({ids})
                ORDER BY id
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AvailabilitySeries>> LockOwnerSeriesAsync(
        UserId ownerId, CancellationToken cancellationToken) =>
        await context.AvailabilitySeries
            .FromSqlInterpolated($"""
                SELECT *
                FROM availability_series
                WHERE owner_id = {ownerId.Value}
                ORDER BY id
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AvailabilityBlock>> LockBlocksAsync(
        IReadOnlyCollection<AvailabilityBlockId> blockIds, CancellationToken cancellationToken)
    {
        if (blockIds.Count == 0)
        {
            return [];
        }

        Guid[] ids = [.. blockIds.Select(id => id.Value)];
        return await context.AvailabilityBlocks
            .FromSqlInterpolated($"""
                SELECT *
                FROM availability_blocks
                WHERE id = ANY({ids})
                ORDER BY id
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AvailabilityBlock>> LockBlocksBySourceSeriesAsync(
        IReadOnlyCollection<AvailabilitySeriesId> seriesIds, CancellationToken cancellationToken)
    {
        if (seriesIds.Count == 0)
        {
            return [];
        }

        Guid[] ids = [.. seriesIds.Select(id => id.Value)];
        return await context.AvailabilityBlocks
            .FromSqlInterpolated($"""
                SELECT *
                FROM availability_blocks
                WHERE source_series_id = ANY({ids})
                ORDER BY id
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AvailabilityBlock>> LockBlocksByOwnerAsync(
        UserId ownerId, CancellationToken cancellationToken) =>
        await context.AvailabilityBlocks
            .FromSqlInterpolated($"""
                SELECT *
                FROM availability_blocks
                WHERE owner_id = {ownerId.Value}
                ORDER BY id
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AvailabilityOccurrenceSkip>> LockSkipsAsync(
        IReadOnlyCollection<AvailabilitySeriesId> seriesIds, CancellationToken cancellationToken)
    {
        if (seriesIds.Count == 0)
        {
            return [];
        }

        Guid[] ids = [.. seriesIds.Select(id => id.Value)];
        return await context.AvailabilityOccurrenceSkips
            .FromSqlInterpolated($"""
                SELECT *
                FROM availability_occurrence_skips
                WHERE series_id = ANY({ids})
                ORDER BY series_id, occurrence_date
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);
    }

    public AvailabilityOccurrenceSkip? FindSkip(
        IReadOnlyList<AvailabilityOccurrenceSkip> lockedSkips, AvailabilitySeriesId seriesId, LocalDate occurrenceDate) =>
        lockedSkips.FirstOrDefault(skip => skip.SeriesId == seriesId && skip.OccurrenceDate == occurrenceDate);

    public AvailabilityBlock? FindMaterializedBlock(
        IReadOnlyList<AvailabilityBlock> lockedBlocks, AvailabilitySeriesId seriesId, LocalDate occurrenceDate) =>
        lockedBlocks.FirstOrDefault(block =>
            block.SourceSeriesId == seriesId && block.SourceOccurrenceDate == occurrenceDate);

    public void AddBlock(AvailabilityBlock block) => context.AvailabilityBlocks.Add(block);

    public void RemoveBlock(AvailabilityBlock block) => context.AvailabilityBlocks.Remove(block);

    public void AddSeries(AvailabilitySeries series) => context.AvailabilitySeries.Add(series);

    public void RemoveSeries(AvailabilitySeries series) => context.AvailabilitySeries.Remove(series);

    public void AddSkip(AvailabilityOccurrenceSkip skip) => context.AvailabilityOccurrenceSkips.Add(skip);

    public void RemoveSkipsFrom(AvailabilitySeriesId seriesId, LocalDate from)
    {
        foreach (AvailabilityOccurrenceSkip tracked in context.ChangeTracker.Entries<AvailabilityOccurrenceSkip>()
            .Select(entry => entry.Entity)
            .Where(skip => skip.SeriesId == seriesId && skip.OccurrenceDate >= from)
            .ToList())
        {
            context.AvailabilityOccurrenceSkips.Remove(tracked);
        }
    }

    public void RemoveMaterializedBlocksFrom(AvailabilitySeriesId seriesId, LocalDate from)
    {
        foreach (AvailabilityBlock tracked in context.ChangeTracker.Entries<AvailabilityBlock>()
            .Select(entry => entry.Entity)
            .Where(block => block.SourceSeriesId == seriesId && block.SourceOccurrenceDate >= from)
            .ToList())
        {
            context.AvailabilityBlocks.Remove(tracked);
        }
    }

    public async Task RetargetSeriesZoneAsync(UserId ownerId, string zoneId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE availability_series
            SET zone_id = {zoneId}
            WHERE owner_id = {ownerId.Value}
            """, cancellationToken);
}
