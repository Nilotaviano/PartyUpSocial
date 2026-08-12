using PartyUp.Api.Auth;
using PartyUp.Api.Contracts.SteamCatalog;
using PartyUp.Application.SteamCatalog;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// STEAM_CATALOG_DESIGN.md §3: the first-party catalog typeahead search, relative to /api/v1.
/// </summary>
public static class SteamCatalogEndpoints
{
    public static RouteGroupBuilder MapSteamCatalogEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/steam-catalog", SearchAsync)
            .WithName(ApiOperationIds.SearchSteamCatalog)
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded)
            .Produces<SteamCatalogSearchResponse>();

        return api;
    }

    private static async Task<IResult> SearchAsync(
        string? query,
        SearchSteamCatalogUseCase useCase,
        CancellationToken cancellationToken)
    {
        SteamCatalogSearchResultSet result = await useCase.ExecuteAsync(query, cancellationToken);
        return Results.Ok(new SteamCatalogSearchResponse(
            result.CatalogAvailable,
            [.. result.Results.Select(match => new SteamCatalogSearchResultView(match.AppId, match.Name))]));
    }
}
