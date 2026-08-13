using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PartyUp.Api.IntegrationTests.Security;

/// <summary>
/// PHASE8_DESIGN.md §3 S9 (Q7): the app-wide hardening headers (nosniff, frame-ancestors via CSP,
/// X-Frame-Options, Permissions-Policy, Referrer-Policy) apply to every response -- API JSON, the
/// <c>/g</c> redirect leg, and even a 404 miss on <c>/a</c>. <c>docs/SECURITY_REVIEW_PHASE8.md</c>
/// records this row. The avatar proxy's stricter route-local CSP is covered separately by
/// <see cref="Avatars.AvatarProxyEndpointTests.ResponseHeadersMatchSpecExactly"/>, which the app-wide
/// middleware must not overwrite.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SecurityHeadersTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ApiResponse_CarriesTheAppWideHardeningHeaders()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health");

        AssertAppWideHeaders(response);
    }

    [Fact]
    public async Task GuestExchangeRedirectLeg_CarriesTheAppWideHardeningHeaders()
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using HttpResponseMessage response = await client.GetAsync("/g/does-not-exist");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        AssertAppWideHeaders(response);
    }

    [Fact]
    public async Task AvatarProxyMiss_StillCarriesTheAppWideHardeningHeaders()
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using HttpResponseMessage response = await client.GetAsync("/a/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertAppWideHeaders(response);

        // No wwwroot in the integration host, so the hash is omitted -- confirms the "no built
        // SPA" branch without needing a filesystem fixture.
        Assert.DoesNotContain("sha256", Single(response, "Content-Security-Policy"));
    }

    private static void AssertAppWideHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Single(response, "X-Frame-Options"));
        Assert.Equal(
            "camera=(), microphone=(), geolocation=(), payment=()",
            Single(response, "Permissions-Policy"));
        Assert.Equal("no-referrer", Single(response, "Referrer-Policy"));

        string? csp = Single(response, "Content-Security-Policy");
        Assert.NotNull(csp);
        Assert.Contains("frame-ancestors 'none'", csp);
    }

    private static string? Single(HttpResponseMessage response, string headerName) =>
        response.Headers.TryGetValues(headerName, out IEnumerable<string>? values)
            ? values.Single()
            : response.Content.Headers.TryGetValues(headerName, out IEnumerable<string>? contentValues)
                ? contentValues.Single()
                : null;
}
