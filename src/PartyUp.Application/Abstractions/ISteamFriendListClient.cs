namespace PartyUp.Application.Abstractions;

public interface ISteamFriendListClient
{
    Task<SteamFriendListResult> GetAsync(
        string steamId64,
        CancellationToken cancellationToken);
}

public abstract record SteamFriendListResult;

public sealed record SteamFriendListNotConfigured : SteamFriendListResult;

public sealed record SteamFriendListPrivateProfile : SteamFriendListResult;

public sealed record SteamFriendListUpstreamFailure : SteamFriendListResult;

public sealed record SteamFriendListSuccess(
    IReadOnlyList<SteamFriend> Friends) : SteamFriendListResult;

public sealed record SteamFriend(
    string SteamId64,
    string? PersonaName,
    string? AvatarUrl);
