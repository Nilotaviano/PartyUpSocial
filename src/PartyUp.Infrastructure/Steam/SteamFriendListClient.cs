using System.Net;
using System.Text.Json;
using PartyUp.Application.Abstractions;

namespace PartyUp.Infrastructure.Steam;

public sealed class SteamFriendListClient(SteamApiClient api, string? apiKey)
    : ISteamFriendListClient
{
    private const int SummaryBatchSize = 100;

    public async Task<SteamFriendListResult> GetAsync(
        string steamId64,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new SteamFriendListNotConfigured();
        }

        string[] friendIds;
        try
        {
            string json = await api.GetStringAsync(
                FriendListUri(apiKey, steamId64),
                cancellationToken);
            if (!TryReadFriendIds(json, out friendIds))
            {
                return new SteamFriendListUpstreamFailure();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SteamFriendListUpstreamFailure();
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            return await ProbeApiKeyAsync(steamId64, cancellationToken)
                ? new SteamFriendListPrivateProfile()
                : new SteamFriendListUpstreamFailure();
        }
        catch (HttpRequestException)
        {
            return new SteamFriendListUpstreamFailure();
        }
        catch (JsonException)
        {
            return new SteamFriendListUpstreamFailure();
        }

        if (friendIds.Length == 0)
        {
            return new SteamFriendListSuccess([]);
        }

        var profiles = new Dictionary<string, SteamSummary>(StringComparer.Ordinal);
        foreach (string[] batch in friendIds.Chunk(SummaryBatchSize))
        {
            IReadOnlyDictionary<string, SteamSummary>? enriched =
                await TryReadSummariesAsync(batch, cancellationToken);
            if (enriched is null)
            {
                continue;
            }

            foreach ((string id, SteamSummary summary) in enriched)
            {
                profiles[id] = summary;
            }
        }

        SteamFriend[] friends = friendIds.Select(id =>
        {
            profiles.TryGetValue(id, out SteamSummary? summary);
            return new SteamFriend(id, summary?.PersonaName, summary?.AvatarUrl);
        }).ToArray();
        return new SteamFriendListSuccess(friends);
    }

    private async Task<bool> ProbeApiKeyAsync(
        string steamId64,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await api.GetStringAsync(
                PlayerSummariesUri(apiKey!, [steamId64]),
                cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            if (!TryGetPlayers(document.RootElement, out JsonElement players))
            {
                return false;
            }

            return players.EnumerateArray().Any(player =>
                player.ValueKind == JsonValueKind.Object &&
                player.TryGetProperty("steamid", out JsonElement id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), steamId64, StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyDictionary<string, SteamSummary>?> TryReadSummariesAsync(
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await api.GetStringAsync(
                PlayerSummariesUri(apiKey!, steamIds),
                cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            if (!TryGetPlayers(document.RootElement, out JsonElement players))
            {
                return null;
            }

            var summaries = new Dictionary<string, SteamSummary>(StringComparer.Ordinal);
            foreach (JsonElement player in players.EnumerateArray())
            {
                if (player.ValueKind != JsonValueKind.Object ||
                    !player.TryGetProperty("steamid", out JsonElement idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(idElement.GetString()))
                {
                    continue;
                }

                string id = idElement.GetString()!;
                string? personaName = ReadOptionalString(player, "personaname");
                string? avatarUrl = ReadOptionalString(player, "avatarfull");
                summaries[id] = new SteamSummary(personaName, avatarUrl);
            }

            return summaries;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadFriendIds(string json, out string[] friendIds)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("friendslist", out JsonElement list) ||
            list.ValueKind != JsonValueKind.Object ||
            !list.TryGetProperty("friends", out JsonElement friends) ||
            friends.ValueKind != JsonValueKind.Array)
        {
            friendIds = [];
            return false;
        }

        var ids = new List<string>();
        foreach (JsonElement friend in friends.EnumerateArray())
        {
            if (friend.ValueKind != JsonValueKind.Object ||
                !friend.TryGetProperty("steamid", out JsonElement id) ||
                id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()))
            {
                friendIds = [];
                return false;
            }

            ids.Add(id.GetString()!);
        }

        friendIds = [.. ids];
        return true;
    }

    private static bool TryGetPlayers(JsonElement root, out JsonElement players)
    {
        players = default;
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("response", out JsonElement response) &&
               response.ValueKind == JsonValueKind.Object &&
               response.TryGetProperty("players", out players) &&
               players.ValueKind == JsonValueKind.Array;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static Uri FriendListUri(string key, string steamId64) =>
        new(
            "https://api.steampowered.com/ISteamUser/GetFriendList/v0001/" +
            $"?key={Uri.EscapeDataString(key)}" +
            $"&steamid={Uri.EscapeDataString(steamId64)}" +
            "&relationship=friend");

    private static Uri PlayerSummariesUri(
        string key,
        IReadOnlyCollection<string> steamIds) =>
        new(
            "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/" +
            $"?key={Uri.EscapeDataString(key)}" +
            $"&steamids={Uri.EscapeDataString(string.Join(',', steamIds))}");

    private sealed record SteamSummary(string? PersonaName, string? AvatarUrl);
}
