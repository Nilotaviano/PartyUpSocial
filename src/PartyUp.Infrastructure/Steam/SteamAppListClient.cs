using System.Text;
using System.Text.Json;
using PartyUp.Application.Abstractions;

namespace PartyUp.Infrastructure.Steam;

/// <summary>
/// STEAM_CATALOG_DESIGN.md §1: <c>IStoreService/GetAppList</c> v1, games only. A fixed first-party
/// Steam host with no user-supplied URL, so the SSRF-pinned handler AVATARS_DESIGN.md §3.2 requires
/// elsewhere is not needed here — same posture as <see cref="SteamProfileClient"/>/
/// <see cref="SteamFriendListClient"/>.
/// </summary>
public sealed class SteamAppListClient(SteamApiClient api, string? apiKey) : ISteamAppListClient
{
    private const int MaxResults = 50_000;

    public async Task<SteamAppListPage> GetAppListPageAsync(
        SteamAppListRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new SteamAppListPage([], null, HaveMoreResults: false);
        }

        string json = await api.GetStringAsync(BuildUri(apiKey, request), cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out JsonElement response) ||
            response.ValueKind != JsonValueKind.Object)
        {
            return new SteamAppListPage([], null, HaveMoreResults: false);
        }

        List<SteamAppListItem> apps = [];
        if (response.TryGetProperty("apps", out JsonElement appsElement) &&
            appsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement app in appsElement.EnumerateArray())
            {
                if (!app.TryGetProperty("appid", out JsonElement appIdElement) ||
                    appIdElement.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                string name = app.TryGetProperty("name", out JsonElement nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;
                long? lastModified = app.TryGetProperty("last_modified", out JsonElement lastModifiedElement) &&
                    lastModifiedElement.ValueKind == JsonValueKind.Number
                        ? lastModifiedElement.GetInt64()
                        : null;

                apps.Add(new SteamAppListItem(appIdElement.GetInt64(), name, lastModified));
            }
        }

        bool haveMore = response.TryGetProperty("have_more_results", out JsonElement haveMoreElement) &&
            haveMoreElement.ValueKind == JsonValueKind.True;
        long? lastAppId = response.TryGetProperty("last_appid", out JsonElement lastAppIdElement) &&
            lastAppIdElement.ValueKind == JsonValueKind.Number
                ? lastAppIdElement.GetInt64()
                : null;

        return new SteamAppListPage(apps, lastAppId, haveMore);
    }

    private static Uri BuildUri(string apiKey, SteamAppListRequest request)
    {
        var query = new StringBuilder("https://api.steampowered.com/IStoreService/GetAppList/v1/?");
        query.Append("key=").Append(Uri.EscapeDataString(apiKey));
        query.Append("&include_games=true&include_dlc=false&include_software=false");
        query.Append("&include_videos=false&include_hardware=false");
        query.Append("&max_results=").Append(MaxResults);
        if (request.LastAppId is { } lastAppId)
        {
            query.Append("&last_appid=").Append(lastAppId);
        }

        if (request.IfModifiedSinceUnixSeconds is { } ifModifiedSince)
        {
            query.Append("&if_modified_since=").Append(ifModifiedSince);
        }

        return new Uri(query.ToString());
    }
}
