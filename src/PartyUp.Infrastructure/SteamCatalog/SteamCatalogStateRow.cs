using NodaTime;

namespace PartyUp.Infrastructure.SteamCatalog;

/// <summary>
/// STEAM_CATALOG_DESIGN.md §2: the single advisory row — queries never gate on it. The migration
/// seeds the one row (<see cref="Id"/> fixed at 1); the job only ever updates it in place.
/// </summary>
public sealed class SteamCatalogStateRow
{
    private SteamCatalogStateRow()
    {
    }

    public SteamCatalogStateRow(Instant? watermark, Instant? lastRunAt, bool lastRunSucceeded)
    {
        Watermark = watermark;
        LastRunAt = lastRunAt;
        LastRunSucceeded = lastRunSucceeded;
    }

    public int Id { get; private set; }
    public Instant? Watermark { get; private set; }
    public Instant? LastRunAt { get; private set; }
    public bool LastRunSucceeded { get; private set; }

    public void Update(Instant? watermark, Instant? lastRunAt, bool lastRunSucceeded)
    {
        Watermark = watermark;
        LastRunAt = lastRunAt;
        LastRunSucceeded = lastRunSucceeded;
    }
}
