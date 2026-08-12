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

        long[] appIds = new long[apps.Count];
        string[] names = new string[apps.Count];
        Instant?[] lastModified = new Instant?[apps.Count];
        for (int i = 0; i < apps.Count; i++)
        {
            appIds[i] = apps[i].AppId;
            names[i] = apps[i].Name;
            lastModified[i] = apps[i].LastModified;
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
