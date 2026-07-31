namespace PartyUp.Domain.Kernel;

/// <summary>
/// The optional game a session or poll is played on (§5.1). Steam supplies either a catalog
/// reference or free text — never both — while PlayStation, Xbox and N/A have no viable public
/// catalog and carry free text only (D-041/D-058/D-176). Both shapes hold a <see cref="GameName"/>
/// under the same bounds, so a roster renders without a catalog round-trip.
/// </summary>
public sealed record SessionGame
{
    private SessionGame(GameName name, long? steamAppId)
    {
        Name = name;
        SteamAppId = steamAppId;
    }

    public GameName Name { get; }

    /// <summary>Non-null exactly when the title came from the Steam catalog rather than a keyboard.</summary>
    public long? SteamAppId { get; }

    public bool IsCatalogReference => SteamAppId is not null;

    /// <summary>D-058/D-176: the fallback every platform accepts, Steam included.</summary>
    public static SessionGame FreeText(GameName name) => new(RequireNamed(name), null);

    /// <summary>
    /// D-041/D-176: a Steam catalog pick. The app id is Steam's, not ours, so it stays a plain
    /// external reference rather than a typed entity id (A-015 governs our own ids).
    /// </summary>
    public static SessionGame FromSteamCatalog(long steamAppId, GameName name)
    {
        if (steamAppId <= 0)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"A Steam app id is a positive number, but was {steamAppId} (D-041)."));
        }

        return new SessionGame(RequireNamed(name), steamAppId);
    }

    /// <summary>
    /// D-176: only Steam offers catalog selection, so a catalog reference on PlayStation, Xbox or
    /// N/A is a validation failure rather than a silently downgraded free-text entry.
    /// </summary>
    public void EnsureAllowedOn(Platform platform)
    {
        if (IsCatalogReference && platform != Platform.Steam)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"{platform} games are free text only; catalog selection is Steam's alone (D-176)."));
        }
    }

    public override string ToString() => Name.Value;

    private static GameName RequireNamed(GameName name) =>
        name.Value.Length >= GameName.MinLength
            ? name
            : throw new DomainException(DomainError.ValidationFailed, "A game, when present, needs a name (D-058).");
}
