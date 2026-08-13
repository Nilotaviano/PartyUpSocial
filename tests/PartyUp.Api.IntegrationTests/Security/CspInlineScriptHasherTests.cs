using System.Security.Cryptography;
using System.Text;
using PartyUp.Api.Security;

namespace PartyUp.Api.IntegrationTests.Security;

/// <summary>
/// PHASE8_DESIGN.md §3 S9 (Q7): the CSP script-src hash is computed at startup from the built
/// wwwroot/index.html, never hardcoded. No filesystem fixture -- reads a plain temp path.
/// </summary>
public sealed class CspInlineScriptHasherTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "csp-hasher-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ComputeShaTokens_ReturnsTheCorrectSha256TokenForAnInlineScriptBody()
    {
        const string ScriptBody = "\n  console.log('theme');\n";
        string html = $"""
            <!doctype html>
            <html>
              <head>
                <script>{ScriptBody}</script>
                <script type="module" src="/src/main.tsx"></script>
              </head>
              <body></body>
            </html>
            """;
        string indexHtmlPath = WriteIndexHtml(html);

        IReadOnlyList<string> tokens = new CspInlineScriptHasher().ComputeShaTokens(indexHtmlPath);

        string expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ScriptBody)));
        Assert.Equal([$"'sha256-{expectedHash}'"], tokens);

        string csp = SecurityHeaders.BuildContentSecurityPolicy(tokens);
        Assert.Contains($"script-src 'self' 'sha256-{expectedHash}'", csp);
    }

    [Fact]
    public void ComputeShaTokens_ReturnsEmpty_WhenWwwrootIndexHtmlDoesNotExist()
    {
        string indexHtmlPath = Path.Combine(_tempDirectory, "does-not-exist", "index.html");

        IReadOnlyList<string> tokens = new CspInlineScriptHasher().ComputeShaTokens(indexHtmlPath);

        Assert.Empty(tokens);

        string csp = SecurityHeaders.BuildContentSecurityPolicy(tokens);
        Assert.DoesNotContain("sha256", csp);
        Assert.Contains("script-src 'self'", csp);
    }

    private string WriteIndexHtml(string html)
    {
        Directory.CreateDirectory(_tempDirectory);
        string path = Path.Combine(_tempDirectory, "index.html");
        File.WriteAllText(path, html);
        return path;
    }
}
