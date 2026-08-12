using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Steam;

public sealed class SteamIdentity
{
    private SteamIdentity()
    {
    }

    public SteamIdentity(UserId userId, string steamId64, string personaName, Instant fetchedAt)
    {
        UserId = userId;
        SteamId64 = steamId64;
        PersonaName = personaName;
        FetchedAt = fetchedAt;
    }

    public UserId UserId { get; private set; }
    public string SteamId64 { get; private set; } = string.Empty;
    public string PersonaName { get; private set; } = string.Empty;
    public Instant FetchedAt { get; private set; }

    public void Update(string steamId64, string personaName, Instant fetchedAt)
    {
        SteamId64 = steamId64;
        PersonaName = personaName;
        FetchedAt = fetchedAt;
    }
}
