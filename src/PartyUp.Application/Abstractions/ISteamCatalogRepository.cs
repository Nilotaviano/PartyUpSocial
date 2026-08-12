using NodaTime;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// STEAM_CATALOG_DESIGN.md §2: the <c>steam_apps</c>/<c>steam_catalog_state</c> read models are
/// Infrastructure-owned, not a Domain aggregate — no visibility-engine involvement (catalog data is
/// public). Search is a trigram similarity query (§3); the upsert is set-based, one call per Steam
/// page (§1).
/// </summary>
public interface ISteamCatalogRepository
{
    /// <summary>
    /// Trigram floor (`%`) plus similarity ranking, ties by name — STEAM_CATALOG_DESIGN.md §3.
    /// </summary>
    Task<IReadOnlyList<SteamCatalogSearchResult>> SearchAsync(
        string term, int limit, CancellationToken cancellationToken);

    /// <summary>Whether <c>steam_apps</c> has any row — the <c>catalogAvailable</c> flag's source.</summary>
    Task<bool> HasAnyAppAsync(CancellationToken cancellationToken);

    /// <summary>One statement for the whole page — never per-row round trips (§1).</summary>
    Task UpsertBatchAsync(IReadOnlyList<SteamCatalogAppUpsert> apps, CancellationToken cancellationToken);

    Task<SteamCatalogState> GetStateAsync(CancellationToken cancellationToken);

    Task SetStateAsync(
        Instant? watermark,
        Instant lastRunAt,
        bool lastRunSucceeded,
        CancellationToken cancellationToken);
}

public sealed record SteamCatalogSearchResult(long AppId, string Name);

public sealed record SteamCatalogAppUpsert(long AppId, string Name, Instant? LastModified);

public sealed record SteamCatalogState(Instant? Watermark, Instant? LastRunAt, bool LastRunSucceeded);
