using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AntiforgeryTests : IAsyncLifetime, IDisposable
{
    private static readonly IReadOnlyDictionary<string, UnsafeRouteCase> UnsafeRoutes =
        new Dictionary<string, UnsafeRouteCase>(StringComparer.Ordinal)
        {
            ["POST /api/v1/auth/register"] = Public(() => JsonContent.Create(new
            {
                email = "not-an-email",
                ageGateAttested = false,
                locale = "en",
            })),
            ["POST /api/v1/auth/login"] = Public(() => JsonContent.Create(new
            {
                email = "not-an-email",
                password = "wrong-password",
            })),
            ["POST /api/v1/auth/magic-link"] = Public(() => JsonContent.Create(new
            {
                email = "not-an-email",
                locale = "en",
            })),
            ["POST /api/v1/auth/magic-link/consume"] = Public(() =>
                JsonContent.Create(new { token = "invalid", ageGateAttested = true })),
            ["POST /api/v1/auth/password/forgot"] = Public(() =>
                JsonContent.Create(new { email = "not-an-email" })),
            ["POST /api/v1/auth/password/reset"] = Public(() => JsonContent.Create(new
            {
                token = "invalid",
                newPassword = "replacement-password",
            })),
            ["POST /api/v1/auth/password/change"] = Protected(() =>
                JsonContent.Create(new
                {
                    currentPassword = "current-password",
                    newPassword = "replacement-password",
                })),
            ["POST /api/v1/auth/password/set"] = Protected(() =>
                JsonContent.Create(new
                {
                    newPassword = "replacement-password",
                    currentPassword = "current-password",
                })),
            ["POST /api/v1/auth/email/change"] = Protected(() =>
                JsonContent.Create(new
                {
                    email = "new-address@example.test",
                    currentPassword = "current-password",
                })),
            ["POST /api/v1/auth/email/change/verify"] = Public(() =>
                JsonContent.Create(new { token = "invalid" })),
            ["POST /api/v1/auth/email/verify/request"] = Protected(() =>
                JsonContent.Create(new { })),
            ["POST /api/v1/auth/email/verify"] = Public(() => JsonContent.Create(new
            {
                token = "invalid",
                password = "replacement-password",
                ageGateAttested = true,
            })),
            ["POST /api/v1/auth/reauth"] = Protected(() =>
                JsonContent.Create(new { currentPassword = "current-password" })),
            ["POST /api/v1/auth/external/intent"] = Public(() =>
                JsonContent.Create(new
                {
                    provider = "google",
                    mode = "login",
                    returnTo = "/app",
                    ageGateAttested = true,
                })),
            ["POST /api/v1/auth/reauth/email"] = Protected(() =>
                JsonContent.Create(new { })),
            ["POST /api/v1/auth/logout"] = Protected(() => null),
            ["POST /api/v1/onboarding/validate"] = Protected(() =>
                JsonContent.Create(new { })),
            ["POST /api/v1/onboarding/complete"] = Protected(() =>
                JsonContent.Create(new
                {
                    username = "MatrixPlayer",
                    displayName = "Matrix Player",
                    avatar = new { kind = "emoji", emoji = "🙂" },
                    timeZone = "America/Fortaleza",
                    locale = "en",
                })),
        };

    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Post_WithoutTheHeader_Fails()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "player@example.test", password = "wrong-password" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("antiforgery_failed", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_WithTheHeader_Succeeds()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new
            {
                email = "player@example.test",
                password = "wrong-password",
            }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(
            "antiforgery_failed",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ReturnsTheRequestTokenInTheBody_AndTheCookieIsHttpOnly()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/antiforgery");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AntiforgeryTokenResponse body =
            (await response.Content.ReadFromJsonAsync<AntiforgeryTokenResponse>())!;
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        SetCookieHeaderValue cookie = response.Headers.GetValues(HeaderNames.SetCookie)
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(value => value.Name.Value == "pu.csrf");
        Assert.True(cookie.HttpOnly);
        Assert.NotEqual(cookie.Value.Value, body.Token);
    }

    [Fact]
    public async Task EveryUnsafeRoute_RequiresAntiforgeryAndItsDocumentedAuthorization()
    {
        UserId userId = await _factory.SeedUserAsync(
            email: "antiforgery-matrix@example.test",
            password: "current-password");
        using HttpClient client = CreateClient();
        string authCookie = await SignInAsync(client, userId);

        IReadOnlyDictionary<string, RouteEndpoint> mapped = MappedUnsafeRoutes();
        Assert.Equal(UnsafeRoutes.Keys.Order(), mapped.Keys.Order());
        foreach ((string key, UnsafeRouteCase route) in UnsafeRoutes)
        {
            bool hasAuthorization = mapped[key].Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .Count > 0;
            Assert.Equal(route.RequiresAuthorization, hasAuthorization);
        }

        SideEffectSnapshot baseline = await SnapshotAsync();
        foreach ((string key, UnsafeRouteCase route) in UnsafeRoutes)
        {
            using HttpRequestMessage request = CreateRequest(key, route.CreateBody());
            if (route.RequiresAuthorization)
            {
                request.Headers.Add(HeaderNames.Cookie, authCookie);
            }

            HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(
                "antiforgery_failed",
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
        Assert.Equal(baseline, await SnapshotAsync());

        foreach ((string key, UnsafeRouteCase route) in UnsafeRoutes)
        {
            HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
                client,
                RouteFromKey(key),
                route.CreateBody());
            string body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("antiforgery_failed", body, StringComparison.Ordinal);
            if (route.RequiresAuthorization)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Contains("unauthorized", body, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("unauthorized", body, StringComparison.Ordinal);
            }
        }
        Assert.Equal(baseline, await SnapshotAsync());
    }

    private HttpClient CreateClient() => _factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    private Dictionary<string, RouteEndpoint> MappedUnsafeRoutes()
    {
        IEnumerable<EndpointDataSource> dataSources =
            _factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
        return dataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
                endpoint.RoutePattern.RawText?.StartsWith(
                    "/api/v1/",
                    StringComparison.Ordinal) == true &&
                !endpoint.RoutePattern.RawText.Contains(
                    "/_slice2/",
                    StringComparison.Ordinal))
            .SelectMany(endpoint =>
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    .Where(method => !HttpMethods.IsGet(method) &&
                        !HttpMethods.IsHead(method) &&
                        !HttpMethods.IsOptions(method) &&
                        !HttpMethods.IsTrace(method))
                    .Select(method => new
                    {
                        Key = $"{method} {endpoint.RoutePattern.RawText}",
                        Endpoint = endpoint,
                    }) ?? [])
            .ToDictionary(value => value.Key, value => value.Endpoint, StringComparer.Ordinal);
    }

    private static HttpRequestMessage CreateRequest(string key, HttpContent? body) =>
        new(HttpMethod.Post, RouteFromKey(key))
        {
            Content = body,
        };

    private static string RouteFromKey(string key) => key[(key.IndexOf(' ') + 1)..];

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Slice5HttpTest.AuthCookie(response);
    }

    private async Task<SideEffectSnapshot> SnapshotAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int tokenCount = await context.Database.SqlQueryRaw<int>(
                """SELECT count(*)::int AS "Value" FROM auth_tokens""")
            .SingleAsync();
        return new SideEffectSnapshot(
            await context.DomainUsers.CountAsync(),
            await context.AuthUsers.CountAsync(),
            tokenCount,
            _factory.EmailSender.Messages.Count);
    }

    private static UnsafeRouteCase Public(Func<HttpContent?> body) => new(false, body);

    private static UnsafeRouteCase Protected(Func<HttpContent?> body) => new(true, body);

    private sealed record UnsafeRouteCase(
        bool RequiresAuthorization,
        Func<HttpContent?> CreateBody);

    private sealed record SideEffectSnapshot(
        int DomainUsers,
        int AuthUsers,
        int Tokens,
        int Emails);
}
