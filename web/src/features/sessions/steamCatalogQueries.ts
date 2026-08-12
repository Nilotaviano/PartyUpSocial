import { client } from '../../api/client'

export type SteamCatalogSearchResult = {
  appId: string
  name: string
}

export type SteamCatalogSearchResponse = {
  catalogAvailable: boolean
  results: SteamCatalogSearchResult[]
}

export const steamCatalogKeys = {
  search: (term: string) => ['steam-catalog-search', term] as const,
}

/**
 * D-194 §3/§4: `GET /api/v1/steam-catalog?query=` — top 20 trigram matches,
 * `catalogAvailable: false` when the first-party cache has no rows yet
 * (client degrades to the manual App ID field on that flag, see
 * `SteamGamePicker`). Terms under 2 characters are the caller's job to
 * avoid sending (the endpoint tolerates it with an empty 200 either way).
 */
export async function searchSteamCatalog(
  term: string,
): Promise<SteamCatalogSearchResponse> {
  const { data } = await client.GET('/api/v1/steam-catalog', {
    params: { query: { query: term } },
  })
  if (!data) throw new Error('Steam catalog search response was empty.')
  return {
    catalogAvailable: data.catalogAvailable,
    results: data.results.map((result) => ({
      appId: String(result.appId),
      name: result.name,
    })),
  }
}
