using System.Globalization;
using SixLabors.Fonts;

namespace PartyUp.Api.Unfurl;

/// <summary>
/// The bundled OFL faces the card is drawn with, loaded once at startup from the application's own
/// output directory. The runtime image carries no system fonts, so a system font lookup would fail
/// there — these two files are the only faces the renderer ever sees.
/// </summary>
public sealed class UnfurlCardFonts
{
    private readonly FontFamily _regular;
    private readonly FontFamily _bold;

    /// <summary>Loads <c>Inter-Regular.ttf</c> and <c>Inter-Bold.ttf</c> from <c>Assets/Fonts</c>.</summary>
    public UnfurlCardFonts()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        _regular = new FontCollection().Add(Path.Combine(directory, "Inter-Regular.ttf"), CultureInfo.InvariantCulture);
        _bold = new FontCollection().Add(Path.Combine(directory, "Inter-Bold.ttf"), CultureInfo.InvariantCulture);
    }

    /// <summary>The body face at <paramref name="size"/> pixels.</summary>
    public Font Regular(float size) => Create(_regular, size);

    /// <summary>The heading face at <paramref name="size"/> pixels.</summary>
    public Font Bold(float size) => Create(_bold, size);

    private static Font Create(FontFamily family, float size)
    {
        FontStyle[] styles = [.. family.GetAvailableStyles()];
        FontStyle style = styles.Contains(FontStyle.Regular) ? FontStyle.Regular : styles[0];
        return family.CreateFont(size, style);
    }
}
