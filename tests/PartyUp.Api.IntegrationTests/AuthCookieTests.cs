using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AuthCookieTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Login_IssuesAnHttpOnlyCookie()
    {
        UserId userId = await _factory.SeedUserAsync();
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);

        SetCookieHeaderValue cookie = AuthCookie(response);
        Assert.Equal("pu.auth", cookie.Name.Value);
        Assert.True(cookie.HttpOnly);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
    }

    [Fact]
    public async Task Logout_ClearsIt()
    {
        UserId userId = await _factory.SeedUserAsync();
        using HttpClient client = CreateClient();
        HttpResponseMessage login = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        string cookiePair = CookiePair(AuthCookie(login));
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/_slice2/sign-out",
            content: null,
            cookiePair);

        SetCookieHeaderValue cleared = AuthCookie(response);
        Assert.Equal(string.Empty, cleared.Value.Value);
        Assert.True(cleared.Expires <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Login_IssuesAPrincipalWithIdRolesAndStampOnly()
    {
        UserId userId = await _factory.SeedUserAsync(admin: true);
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        SetCookieHeaderValue cookie = AuthCookie(response);

        using IServiceScope scope = _factory.Services.CreateScope();
        var cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var identityOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<IdentityOptions>>()
            .Value;
        var ticket = cookieOptions.TicketDataFormat.Unprotect(cookie.Value.Value!);

        Assert.NotNull(ticket);
        string[] claimTypes = ticket.Principal.Claims.Select(claim => claim.Type).ToArray();
        Assert.Equal(
            [
                identityOptions.ClaimsIdentity.UserIdClaimType,
                identityOptions.ClaimsIdentity.RoleClaimType,
                identityOptions.ClaimsIdentity.SecurityStampClaimType,
            ],
            claimTypes);
        Assert.DoesNotContain(ClaimTypes.Email, claimTypes);
    }

    private HttpClient CreateClient() => _factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    private static SetCookieHeaderValue AuthCookie(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        string[] values = response.Headers.GetValues(HeaderNames.SetCookie).ToArray();
        return values.Select(value => SetCookieHeaderValue.Parse(value))
            .Single(cookie => cookie.Name.Value == "pu.auth");
    }

    private static string CookiePair(SetCookieHeaderValue cookie) =>
        $"{cookie.Name.Value}={cookie.Value.Value}";
}
