using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PartyUp.Api.Unfurl;

/// <summary>
/// Draws D-224's 1200x630 preview PNG. Colours are the light theme's brand tokens
/// (<c>web/src/index.css</c>); text is clamped to the space it has rather than overflowing, and the
/// output depends only on the card, so two links in the same state render byte-identical images.
/// </summary>
public sealed class UnfurlCardRenderer(UnfurlCardFonts fonts)
{
    /// <summary>The Open Graph large-image size every allowlisted crawler expects.</summary>
    public const int Width = 1200;

    /// <summary>The Open Graph large-image size every allowlisted crawler expects.</summary>
    public const int Height = 630;

    private static readonly Color Background = Color.ParseHex("F6F2E3");
    private static readonly Color Surface = Color.ParseHex("FFFDF5");
    private static readonly Color Ink = Color.ParseHex("20301C");
    private static readonly Color InkSoft = Color.ParseHex("5F6B58");
    private static readonly Color Primary = Color.ParseHex("7CB518");
    private static readonly Color PrimaryInk = Color.ParseHex("1B2408");

    private const float Margin = 88f;
    private const float TextWidth = Width - (2 * Margin);

    /// <summary>Renders <paramref name="card"/> and returns the encoded PNG bytes.</summary>
    public byte[] Render(UnfurlCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        using var image = new Image<Rgba32>(Width, Height, Background.ToPixel<Rgba32>());
        image.Mutate(context =>
        {
            context.Fill(Primary, new RectangleF(0, 0, Width, 14));
            context.Fill(Surface, new RectangleF(56, 62, Width - 112, Height - 124));
            DrawWordmark(context);
            DrawBody(context, card);
        });

        using var buffer = new MemoryStream();
        image.Save(buffer, new PngEncoder());
        return buffer.ToArray();
    }

    private void DrawWordmark(IImageProcessingContext context)
    {
        var pill = new RectangleF(Margin, 96, 232, 66);
        context.Fill(Primary, pill);
        context.DrawText(
            new RichTextOptions(fonts.Bold(36))
            {
                Origin = new PointF(pill.X + (pill.Width / 2f), pill.Y + (pill.Height / 2f)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            "PartyUp",
            PrimaryInk);
    }

    private void DrawBody(IImageProcessingContext context, UnfurlCard card)
    {
        float y = 210f;
        RichTextOptions heading = HeadingOptions(y);
        string headingText = card.Kind == UnfurlCardKind.Unavailable
            ? card.PrimaryLine ?? card.Title
            : card.Title;
        headingText = ClampToLines(headingText, heading, maxLines: 2);
        context.DrawText(heading, headingText, Ink);

        if (card.Kind == UnfurlCardKind.Unavailable)
        {
            return;
        }

        y += TextMeasurer.MeasureSize(headingText, heading).Height + 34f;
        y = DrawLine(context, card.GameName, y, fonts.Regular(36), InkSoft, 14f);
        y = DrawLine(context, card.PrimaryLine, y, fonts.Bold(40), Ink, 12f);
        _ = DrawLine(context, card.SecondaryLine, y, fonts.Regular(34), InkSoft, 0f);
    }

    private static float DrawLine(
        IImageProcessingContext context,
        string? text,
        float y,
        Font font,
        Color color,
        float gap)
    {
        if (string.IsNullOrEmpty(text))
        {
            return y;
        }

        var options = new RichTextOptions(font)
        {
            Origin = new PointF(Margin, y),
            WrappingLength = TextWidth,
        };
        string clamped = ClampToLines(text, options, maxLines: 1);
        context.DrawText(options, clamped, color);
        return y + TextMeasurer.MeasureSize(clamped, options).Height + gap;
    }

    private RichTextOptions HeadingOptions(float y) => new(fonts.Bold(62))
    {
        Origin = new PointF(Margin, y),
        WrappingLength = TextWidth,
        LineSpacing = 1.15f,
    };

    /// <summary>
    /// Shortens <paramref name="text"/> until it wraps into at most <paramref name="maxLines"/>
    /// lines at the given options, re-adding the ellipsis on every attempt.
    /// </summary>
    private static string ClampToLines(string text, RichTextOptions options, int maxLines)
    {
        string candidate = text;
        while (TextMeasurer.CountLines(candidate, options) > maxLines && candidate.Length > 4)
        {
            candidate = UnfurlText.Clamp(candidate, Math.Max(4, candidate.Length - 4));
        }

        return candidate;
    }
}
