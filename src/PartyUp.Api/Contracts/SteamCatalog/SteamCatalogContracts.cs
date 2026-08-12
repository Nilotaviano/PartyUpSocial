namespace PartyUp.Api.Contracts.SteamCatalog;

/// <summary>STEAM_CATALOG_DESIGN.md §3: the search response's wire shape.</summary>
public sealed record SteamCatalogSearchResponse(
    bool CatalogAvailable, IReadOnlyList<SteamCatalogSearchResultView> Results);

public sealed record SteamCatalogSearchResultView(long AppId, string Name);
