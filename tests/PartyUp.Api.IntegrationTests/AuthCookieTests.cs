using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
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

    [Fact]
    public async Task Login_IssuesTheExactProductionCookieShape_AndChallengesStayJson()
    {
        const string email = "production-cookie@example.test";
        const string password = "correct-password";
        await _factory.SeedUserAsync(email: email, password: password);
        await using var production = _factory.WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production"));
        using HttpClient productionClient = production.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false,
            });
        Assert.Equal(
            "Production",
            production.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);

        HttpResponseMessage antiforgery = await productionClient.GetAsync(
            "/api/v1/auth/antiforgery");
        antiforgery.EnsureSuccessStatusCode();
        AntiforgeryTokenResponse antiforgeryBody =
            (await antiforgery.Content.ReadFromJsonAsync<AntiforgeryTokenResponse>())!;
        SetCookieHeaderValue csrf = ParseCookie(antiforgery, "pu.csrf");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        loginRequest.Headers.Add("X-CSRF-TOKEN", antiforgeryBody.Token);
        loginRequest.Headers.Add(HeaderNames.Cookie, CookiePair(csrf));
        HttpResponseMessage login = await productionClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        SetCookieHeaderValue auth = ParseCookie(login, "pu.auth");

        Assert.True(auth.HttpOnly);
        Assert.True(auth.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, auth.SameSite);
        Assert.Equal("/", auth.Path.Value);
        Assert.NotNull(auth.Expires);
        TimeSpan authLifetime = auth.Expires!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(authLifetime, TimeSpan.FromDays(29.9), TimeSpan.FromDays(30.1));
        Assert.Null(auth.MaxAge);

        Assert.True(csrf.HttpOnly);
        Assert.True(csrf.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, csrf.SameSite);
        Assert.Equal("/", csrf.Path.Value);
        Assert.Null(csrf.Expires);
        Assert.Null(csrf.MaxAge);

        using (IServiceScope scope = production.Services.CreateScope())
        {
            CookieAuthenticationOptions options = scope.ServiceProvider
                .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(IdentityConstants.ApplicationScheme);
            Assert.Equal(TimeSpan.FromDays(30), options.ExpireTimeSpan);
            Assert.True(options.SlidingExpiration);
        }

        using HttpClient challengeClient = CreateClient();
        HttpResponseMessage challenged = await challengeClient.GetAsync(
            "/api/v1/_slice2/authenticated");
        await AssertJsonAuthorizationFailureAsync(
            challenged,
            HttpStatusCode.Unauthorized,
            "unauthorized");

        UserId onboarding = await _factory.SeedUserAsync(completeProfile: false);
        HttpResponseMessage signIn = await AntiforgeryTestClient.PostAsync(
            challengeClient,
            $"/api/v1/_slice2/sign-in/{onboarding.Value:D}",
            content: null);
        string onboardingCookie = CookiePair(ParseCookie(signIn, "pu.auth"));
        using var forbiddenRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/_slice2/onboarded");
        forbiddenRequest.Headers.Add(HeaderNames.Cookie, onboardingCookie);
        HttpResponseMessage forbidden = await challengeClient.SendAsync(forbiddenRequest);
        await AssertJsonAuthorizationFailureAsync(
            forbidden,
            HttpStatusCode.Forbidden,
            "onboarding_required");
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

    private static SetCookieHeaderValue ParseCookie(
        HttpResponseMessage response,
        string name) =>
        response.Headers.GetValues(HeaderNames.SetCookie)
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(cookie => cookie.Name.Value == name);

    private static async Task AssertJsonAuthorizationFailureAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain(
            "text/html",
            response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Dictionary<string, string> body =
            (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!;
        Assert.Equal(expectedCode, body["code"]);
    }

    private static string CookiePair(SetCookieHeaderValue cookie) =>
        $"{cookie.Name.Value}={cookie.Value.Value}";
}
