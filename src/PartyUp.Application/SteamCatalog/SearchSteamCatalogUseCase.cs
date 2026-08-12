using PartyUp.Application.Abstractions;

namespace PartyUp.Application.SteamCatalog;

/// <summary>
/// STEAM_CATALOG_DESIGN.md §3: <c>query</c> shorter than 2 chars returns an empty 200 (typeahead
/// ergonomics, not an error), top 20 otherwise. <see cref="SteamCatalogSearchResultSet.CatalogAvailable"/>
/// is computed independently of the query length — the client's D-058 degradation flag must be correct
/// even on a too-short query.
/// </summary>
public sealed class SearchSteamCatalogUseCase(ISteamCatalogRepository catalog)
{
    public const int MinQueryLength = 2;
    public const int ResultLimit = 20;

    public async Task<SteamCatalogSearchResultSet> ExecuteAsync(
        string? query, CancellationToken cancellationToken)
    {
        bool catalogAvailable = await catalog.HasAnyAppAsync(cancellationToken);

        string trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length < MinQueryLength)
        {
            return new SteamCatalogSearchResultSet(catalogAvailable, []);
        }

        IReadOnlyList<SteamCatalogSearchResult> results =
            await catalog.SearchAsync(trimmed, ResultLimit, cancellationToken);
        return new SteamCatalogSearchResultSet(catalogAvailable, results);
    }
}

public sealed record SteamCatalogSearchResultSet(
    bool CatalogAvailable, IReadOnlyList<SteamCatalogSearchResult> Results);
