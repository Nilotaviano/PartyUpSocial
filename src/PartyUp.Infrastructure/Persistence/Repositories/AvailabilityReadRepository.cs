using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Availability;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The availability read adapter (PHASE5_DESIGN.md §5.1, A-076). <see cref="ReadOwnersWindowAsync"/>
/// issues exactly four SQL statements for the whole owner set, never one per owner — the number the
/// A-079 budget is stated in. It returns raw rows only: no recurrence expansion, no merge, no overlap
/// ranking (A-076) — that is <c>AvailabilityProjection</c>/<c>OverlapEngine</c>'s job, in Application.
/// </summary>
public sealed class AvailabilityReadRepository(AppDbContext context) : IAvailabilityReadRepository
{
    public async Task<IReadOnlyList<AvailabilityOwnerData>> ReadOwnersWindowAsync(
        IReadOnlyCollection<UserId> ownerIds, TimeRange window, CancellationToken cancellationToken)
    {
        if (ownerIds.Count == 0)
        {
            return [];
        }

        List<UserId> ownerIdList = [.. ownerIds];
        Guid[] ids = [.. ownerIdList.Select(id => id.Value)];
        Instant from = window.Start;
        Instant to = window.End;

        // Statement 1: the owners' current TimeZone from users.
        List<OwnerZoneRow> zones = await context.DomainUsers
            .AsNoTracking()
            .Where(user => ownerIdList.Contains(user.Id) && user.TimeZone != null)
            .Select(user => new OwnerZoneRow(user.Id, user.TimeZone!))
            .ToListAsync(cancellationToken);

        // Statement 2: one-time blocks intersecting the window — absolute instants, no pad needed.
        List<AvailabilityBlock> blocks = await context.AvailabilityBlocks
            .AsNoTracking()
            .Where(block => ownerIdList.Contains(block.OwnerId) && block.Start < to && block.End > from)
            .ToListAsync(cancellationToken);

        // Statement 3: series whose [EffectiveFrom, EffectiveUntil) intersects the A-076-padded
        // window. The predicate below is A-076's mandated form, verbatim, with the explicit
        // `AT TIME ZONE 'UTC'` casts it requires — a bare `::date` cast resolves under the session's
        // `TimeZone` GUC instead.
        List<AvailabilitySeries> series = await context.AvailabilitySeries
            .FromSqlInterpolated($"""
                SELECT *
                FROM availability_series
                WHERE owner_id = ANY({ids})
                  AND effective_from < (({to} AT TIME ZONE 'UTC')::date + 3)
                  AND (effective_until IS NULL OR effective_until > (({from} AT TIME ZONE 'UTC')::date - 3))
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Statement 4: those series' skips.
        List<AvailabilitySeriesId> seriesIds = [.. series.Select(s => s.Id)];
        List<AvailabilityOccurrenceSkip> skips = seriesIds.Count == 0
            ? []
            : await context.AvailabilityOccurrenceSkips
                .AsNoTracking()
                .Where(skip => seriesIds.Contains(skip.SeriesId))
                .ToListAsync(cancellationToken);

        List<AvailabilityOwnerData> result = [];
        foreach (OwnerZoneRow zone in zones)
        {
            result.Add(new AvailabilityOwnerData(
                zone.OwnerId,
                zone.Zone,
                [.. blocks.Where(block => block.OwnerId == zone.OwnerId)],
                [.. series.Where(s => s.OwnerId == zone.OwnerId)],
                [.. skips.Where(skip => series.Any(s => s.Id == skip.SeriesId && s.OwnerId == zone.OwnerId))]));
        }

        return result;
    }

    public async Task<IReadOnlyList<AvailabilitySeries>> ReadOwnerSeriesAsync(
        UserId ownerId, Instant notEndedBefore, CancellationToken cancellationToken)
    {
        Guid[] ids = [ownerId.Value];

        // Only the trailing half of A-076's predicate: an owner's own rule list has no upper bound,
        // so a series that starts after the requested window is still returned. The `AT TIME ZONE
        // 'UTC'` cast is required for the same reason it is above — a bare `::date` resolves under
        // the session's `TimeZone` GUC.
        List<AvailabilitySeries> series = await context.AvailabilitySeries
            .FromSqlInterpolated($"""
                SELECT *
                FROM availability_series
                WHERE owner_id = ANY({ids})
                  AND (effective_until IS NULL OR effective_until > (({notEndedBefore} AT TIME ZONE 'UTC')::date - 3))
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. series
            .Where(s => !s.IsEmpty)
            .OrderBy(s => s.DayOfWeek)
            .ThenBy(s => s.StartTime)];
    }

    private sealed record OwnerZoneRow(UserId OwnerId, DateTimeZone Zone);
}
