using System.Text.Json;
using PartyUp.Application.Abstractions;

namespace PartyUp.Infrastructure.Steam;

public sealed class SteamProfileClient(SteamApiClient api, string? apiKey) : ISteamProfileClient
{
    public async Task<SteamProfile?> GetProfileAsync(
        string steamId64,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        string url = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/" +
                     $"?key={Uri.EscapeDataString(apiKey)}&steamids={Uri.EscapeDataString(steamId64)}";
        string json = await api.GetStringAsync(new Uri(url), cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out JsonElement response) ||
            !response.TryGetProperty("players", out JsonElement players) ||
            players.ValueKind != JsonValueKind.Array ||
            players.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement player = players[0];
        string? displayName = player.TryGetProperty("personaname", out JsonElement persona)
            ? persona.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        string? avatarUrl = player.TryGetProperty("avatarfull", out JsonElement avatar)
            ? avatar.GetString()
            : null;
        return new SteamProfile(displayName, avatarUrl);
    }
}
