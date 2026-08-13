using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PartyUp.Api.Security;

/// <summary>
/// PHASE8_DESIGN.md §3 S9 (Q7): computes the CSP <c>script-src</c> hash tokens for
/// <c>web/index.html</c>'s inline pre-paint theme script at startup, from the built
/// <c>wwwroot/index.html</c> -- never a hardcoded hash, which would rot on the next Vite build.
/// Reads from a path parameter (not a fixed convention path) so it can be unit tested without a
/// filesystem fixture.
/// </summary>
public sealed partial class CspInlineScriptHasher
{
    /// <summary>
    /// Returns one <c>'sha256-&lt;base64&gt;'</c> token per non-empty inline <c>&lt;script&gt;</c>
    /// body (scripts carrying a <c>src</c> attribute are external, not inline, and are skipped).
    /// Returns an empty list when <paramref name="indexHtmlPath"/> does not exist -- <c>wwwroot</c>
    /// exists only in the container image; <c>dotnet run</c> and the integration test host have
    /// none, and the header still ships without the hash tokens in that topology.
    /// </summary>
    public IReadOnlyList<string> ComputeShaTokens(string indexHtmlPath)
    {
        if (!File.Exists(indexHtmlPath))
        {
            return [];
        }

        string html = File.ReadAllText(indexHtmlPath);
        List<string> tokens = [];
        foreach (Match match in InlineScriptPattern().Matches(html))
        {
            string body = match.Groups["body"].Value;
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(body));
            tokens.Add($"'sha256-{Convert.ToBase64String(hash)}'");
        }

        return tokens;
    }

    [GeneratedRegex(
        @"<script(?![^>]*\ssrc\s*=)[^>]*>(?<body>.*?)</script>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineScriptPattern();
}
