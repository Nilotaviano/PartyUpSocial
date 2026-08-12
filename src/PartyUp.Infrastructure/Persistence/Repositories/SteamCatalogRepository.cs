using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Infrastructure.SteamCatalog;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>STEAM_CATALOG_DESIGN.md §2/§3: plain EF mapping, no visibility engine involvement.</summary>
public sealed class SteamCatalogRepository(AppDbContext context) : ISteamCatalogRepository
{
    public async Task<IReadOnlyList<SteamCatalogSearchResult>> SearchAsync(
        string term, int limit, CancellationToken cancellationToken)
    {
        List<SteamCatalogApp> matches = await context.SteamApps
            .FromSqlInterpolated($"""
                SELECT app_id, name, last_modified
                FROM steam_apps
                WHERE lower(name) % lower({term})
                ORDER BY similarity(lower(name), lower({term})) DESC, name ASC
                LIMIT {limit}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. matches.Select(app => new SteamCatalogSearchResult(app.AppId, app.Name))];
    }

    public Task<bool> HasAnyAppAsync(CancellationToken cancellationToken) =>
        context.SteamApps.AsNoTracking().AnyAsync(cancellationToken);

    public async Task UpsertBatchAsync(
        IReadOnlyList<SteamCatalogAppUpsert> apps, CancellationToken cancellationToken)
    {
        if (apps.Count == 0)
        {
            return;
        }

        // A single unnest INSERT ... ON CONFLICT statement cannot affect the
        // same row twice ("ON CONFLICT DO UPDATE command cannot affect row a
        // second time"). Steam's paged AppList can repeat an app_id within a
        // page, so dedupe last-wins before building the statement's arrays.
        List<SteamCatalogAppUpsert> deduped = [.. apps
            .GroupBy(app => app.AppId)
            .Select(group => group.Last())];

        long[] appIds = new long[deduped.Count];
        string[] names = new string[deduped.Count];
        Instant?[] lastModified = new Instant?[deduped.Count];
        for (int i = 0; i < deduped.Count; i++)
        {
            appIds[i] = deduped[i].AppId;
            names[i] = deduped[i].Name;
            lastModified[i] = deduped[i].LastModified;
        }

        // Set-based, one statement per page (STEAM_CATALOG_DESIGN.md §1) -- never per-row round trips.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO steam_apps (app_id, name, last_modified)
            SELECT * FROM unnest({appIds}, {names}, {lastModified})
            ON CONFLICT (app_id) DO UPDATE
            SET name = excluded.name, last_modified = excluded.last_modified
            """,
            cancellationToken);
    }

    public async Task<SteamCatalogState> GetStateAsync(CancellationToken cancellationToken)
    {
        SteamCatalogStateRow? row = await context.SteamCatalogState
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        return row is null
            ? new SteamCatalogState(null, null, false)
            : new SteamCatalogState(row.Watermark, row.LastRunAt, row.LastRunSucceeded);
    }

    public async Task SetStateAsync(
        Instant? watermark,
        Instant lastRunAt,
        bool lastRunSucceeded,
        CancellationToken cancellationToken)
    {
        SteamCatalogStateRow? row = await context.SteamCatalogState.SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            context.SteamCatalogState.Add(new SteamCatalogStateRow(watermark, lastRunAt, lastRunSucceeded));
        }
        else
        {
            row.Update(watermark, lastRunAt, lastRunSucceeded);
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
