using PartyUp.Api.Unfurl;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// D-224's text handling, exercised without a host: CR/LF never survive into the card, long titles
/// come back clamped with an ellipsis, and the clamped value is what the escaper is handed.
/// </summary>
public sealed class UnfurlTextTests
{
    [Fact]
    public void Sanitize_StripsLineBreaksAndCollapsesWhitespace_D224()
    {
        Assert.Equal("Raid night Friday", UnfurlText.Sanitize("  Raid\r\nnight\t\tFriday  "));
        Assert.Equal(string.Empty, UnfurlText.Sanitize(null));
    }

    [Fact]
    public void Clamp_KeepsShortValuesAndEllipsizesLongOnesAtAWordBoundary_D224()
    {
        Assert.Equal("Raid night", UnfurlText.Clamp("Raid night", 20));

        string clamped = UnfurlText.Clamp("Friday night deep rock galactic marathon", 20);
        Assert.True(clamped.Length <= 20);
        Assert.EndsWith("…", clamped, StringComparison.Ordinal);
        Assert.StartsWith("Friday night", clamped, StringComparison.Ordinal);
    }

    [Fact]
    public void Clamp_CutsMidWordWhenTheFirstWordExceedsTheBudget_D224()
    {
        string clamped = UnfurlText.Clamp("Supercalifragilisticexpialidocious", 10);

        Assert.Equal(10, clamped.Length);
        Assert.EndsWith("…", clamped, StringComparison.Ordinal);
    }

    [Fact]
    public void JoinParts_DropsEmptyPartsSoAGamelessSessionHasNoDanglingSeparator_D224()
    {
        Assert.Equal("Fri 21 Aug · 2 going", UnfurlText.JoinParts(null, "Fri 21 Aug", "2 going"));
        Assert.Equal("2 going", UnfurlText.JoinParts(null, string.Empty, "2 going"));
    }

    [Fact]
    public void Resolve_MapsStoredTagsOntoTheThreeSupportedLocales_D224()
    {
        Assert.Equal(UnfurlLocale.BrazilianPortuguese, UnfurlStrings.Resolve("pt-BR"));
        Assert.Equal(UnfurlLocale.Spanish, UnfurlStrings.Resolve("es-419"));
        Assert.Equal(UnfurlLocale.English, UnfurlStrings.Resolve("de-DE"));
        Assert.Equal(UnfurlLocale.English, UnfurlStrings.Resolve(null));
    }

    [Fact]
    public void Build_EscapesEveryUserAuthoredValueItInterpolates_D224()
    {
        var card = new UnfurlCard(
            UnfurlCardKind.Session,
            "Raid <b>&\"night\"</b>",
            GameName: null,
            "Fri 21 Aug · 19:00 UTC+00:00",
            "2 going",
            UnfurlLocale.English);

        string html = UnfurlDocument.Build(card, "https://partyup.social/g/abc", "https://partyup.social/g/abc/card.png");

        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;&amp;&quot;night&quot;&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.StartsWith("<!doctype html><html><head>", html, StringComparison.Ordinal);
        Assert.EndsWith("</head><body></body></html>", html, StringComparison.Ordinal);
    }
}
