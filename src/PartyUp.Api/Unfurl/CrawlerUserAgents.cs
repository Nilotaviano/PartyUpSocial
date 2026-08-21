namespace PartyUp.Api.Unfurl;

/// <summary>
/// D-224's fixed link-preview crawler allowlist. Membership is a case-insensitive substring test on
/// the request's <c>User-Agent</c>; anything not on this list — Googlebot included — takes the
/// unchanged cookie-and-redirect exchange path.
/// </summary>
public static class CrawlerUserAgents
{
    private static readonly string[] Tokens =
    [
        "Discordbot",
        "WhatsApp",
        "Twitterbot",
        "facebookexternalhit",
        "Slackbot",
        "TelegramBot",
        "LinkedInBot",
    ];

    /// <summary>Whether <paramref name="userAgent"/> names one of the allowlisted crawlers.</summary>
    public static bool IsCrawler(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return false;
        }

        foreach (string token in Tokens)
        {
            if (userAgent.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
