using System.Collections.Immutable;

namespace PartyUp.Application.Profile;

/// <summary>
/// AVATARS_DESIGN.md §2.1: the closed 24-glyph catalog is the server-side allowlist for emoji
/// avatars (D-050). It lives in Application, not Domain: <see cref="PartyUp.Domain.Users.Avatar"/>
/// documents charset policy as application-layer, and Domain may reference nothing but NodaTime.
/// The order is the picker's render order and the migration's hash-index order — do not reorder.
/// </summary>
public static class AvatarCatalog
{
    public static ImmutableArray<string> Glyphs { get; } =
    [
        "\U0001F3AE", // gameController
        "\U0001F3B2", // dice
        "\U0001F579\uFE0F", // joystick
        "\U0001F47E", // alien
        "\U0001F0CF", // jokerCard
        "\u265F\uFE0F", // chessPawn
        "\U0001F3AF", // dart
        "\U0001F3C6", // trophy
        "\U0001F680", // rocket
        "\U0001F409", // dragon
        "\U0001F98A", // fox
        "\U0001F419", // octopus
        "\U0001F427", // penguin
        "\U0001F989", // owl
        "\U0001F41D", // bee
        "\U0001F335", // cactus
        "\U0001F344", // mushroom
        "\U0001F319", // moon
        "\u2B50", // star
        "\U0001F525", // fire
        "\u26A1", // bolt
        "\U0001F3A7", // headphones
        "\U0001F355", // pizza
        "\u2615", // coffee
    ];

    public static bool Contains(string glyph) => Glyphs.Contains(glyph, StringComparer.Ordinal);
}
