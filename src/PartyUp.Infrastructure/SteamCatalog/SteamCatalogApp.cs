using NodaTime;

namespace PartyUp.Infrastructure.SteamCatalog;

/// <summary>
/// STEAM_CATALOG_DESIGN.md §2: an Infrastructure-owned read model, not a Domain aggregate — catalog
/// data is public, so no visibility engine is involved. Rows are never deleted (§1): a stale
/// <see cref="Name"/> is harmless because free text always exists as a fallback (D-058).
/// </summary>
public sealed class SteamCatalogApp
{
    private SteamCatalogApp()
    {
    }

    public SteamCatalogApp(long appId, string name, Instant? lastModified)
    {
        AppId = appId;
        Name = name;
        LastModified = lastModified;
    }

    public long AppId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Instant? LastModified { get; private set; }
}
