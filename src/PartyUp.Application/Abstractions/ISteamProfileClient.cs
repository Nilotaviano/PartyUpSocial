namespace PartyUp.Application.Abstractions;

public interface ISteamProfileClient
{
    Task<SteamProfile?> GetProfileAsync(string steamId64, CancellationToken cancellationToken);
}

public sealed record SteamProfile(string DisplayName, string? AvatarUrl);
