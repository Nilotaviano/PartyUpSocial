using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Auth;
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
            ["POST /api/v1/auth/password/change"] = Authenticated(() =>
                JsonContent.Create(new
                {
                    currentPassword = "current-password",
                    newPassword = "replacement-password",
                })),
            ["POST /api/v1/auth/password/set"] = Authenticated(() =>
                JsonContent.Create(new
                {
                    newPassword = "replacement-password",
                    currentPassword = "current-password",
                })),
            ["POST /api/v1/auth/email/change"] = Authenticated(() =>
                JsonContent.Create(new
                {
                    email = "new-address@example.test",
                    currentPassword = "current-password",
                })),
            ["POST /api/v1/auth/email/change/verify"] = Public(() =>
                JsonContent.Create(new { token = "invalid" })),
            ["POST /api/v1/auth/email/verify/request"] = Authenticated(() =>
                JsonContent.Create(new { })),
            ["POST /api/v1/auth/email/verify"] = Public(() => JsonContent.Create(new
            {
                token = "invalid",
                password = "replacement-password",
                ageGateAttested = true,
            })),
            ["POST /api/v1/auth/reauth"] = Authenticated(() =>
                JsonContent.Create(new { currentPassword = "current-password" })),
            ["POST /api/v1/auth/external/intent"] = Public(() =>
                JsonContent.Create(new
                {
                    provider = "google",
                    mode = "login",
                    returnTo = "/app",
                    ageGateAttested = true,
                })),
            ["POST /api/v1/auth/reauth/email"] = Authenticated(() =>
                JsonContent.Create(new { })),
            ["DELETE /api/v1/auth/methods/{kind}"] = Authenticated(() =>
                JsonContent.Create(new { currentPassword = "current-password" })),
            ["POST /api/v1/auth/logout"] = Authenticated(() => null),
            ["POST /api/v1/onboarding/validate"] = Authenticated(() =>
                JsonContent.Create(new { })),
            ["POST /api/v1/onboarding/complete"] = Authenticated(() =>
                JsonContent.Create(new
                {
                    username = "MatrixPlayer",
                    displayName = "Matrix Player",
                    avatar = new { kind = "emoji", emoji = "🙂" },
                    timeZone = "America/Fortaleza",
                    locale = "en",
                })),
            ["PUT /api/v1/me/username"] = Onboarded(() =>
                JsonContent.Create(new { username = "MatrixPlayerUpdated" })),
            ["PUT /api/v1/me/display-name"] = Onboarded(() =>
                JsonContent.Create(new { displayName = "Matrix Player" })),
            ["PUT /api/v1/me/avatar"] = Onboarded(() =>
                JsonContent.Create(new { kind = "emoji", emoji = "\U0001F642" })),
            ["PUT /api/v1/me/time-zone"] = Onboarded(() =>
                JsonContent.Create(new { timeZone = "America/Fortaleza" })),
            ["PUT /api/v1/me/locale"] = Onboarded(() =>
                JsonContent.Create(new { locale = "en" })),
            ["PUT /api/v1/me/reminder-default"] = Onboarded(() =>
                JsonContent.Create(new { reminderDefault = "Min15" })),
            ["POST /api/v1/me/deletion"] = Authenticated(() =>
                JsonContent.Create(new { currentPassword = "current-password" })),
            ["POST /api/v1/me/platform-links"] = Authenticated(() =>
                JsonContent.Create(new { platform = "PlayStation", handle = "Player" })),
            ["PATCH /api/v1/me/platform-links/{id:guid}"] = Authenticated(() =>
                JsonContent.Create(new { handle = "Player" })),
            ["DELETE /api/v1/me/platform-links/{id:guid}"] = Authenticated(() => null),
            ["POST /api/v1/account-deletion-requests"] = Public(() =>
                JsonContent.Create(new { email = "not-an-email" })),
            ["POST /api/v1/account-deletion-requests/confirm"] = Public(() =>
                JsonContent.Create(new { token = "invalid" })),
            ["POST /api/v1/friend-requests"] = Onboarded(() =>
                JsonContent.Create(new { targetUsername = "MatrixPlayer" })),
            ["POST /api/v1/friend-requests/{friendshipId}/accept"] = Onboarded(() => null),
            ["POST /api/v1/friend-requests/{friendshipId}/reject"] = Onboarded(() => null),
            ["DELETE /api/v1/friend-requests/{friendshipId}"] = Onboarded(() => null),
            ["DELETE /api/v1/friends/{friendshipId}"] = Onboarded(() => null),
            ["POST /api/v1/blocks"] = Onboarded(() =>
                JsonContent.Create(new { targetUsername = "MatrixPlayer" })),
            ["DELETE /api/v1/blocks/{blockId}"] = Onboarded(() => null),
            ["POST /api/v1/admin/users/{id:guid}/ban"] = Admin(() =>
                JsonContent.Create(new { removeContent = false })),
            ["POST /api/v1/admin/users/{id:guid}/unban"] = Admin(() => null),
            ["POST /api/v1/admin/maintenance/deletion-sweep"] = Admin(() => null),
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
        using JsonDocument problem = JsonDocument.Parse(body);
        Assert.Equal(
            ["type", "title", "status", "detail", "instance", "code", "traceId"],
            problem.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            "The antiforgery token is invalid.",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal("antiforgery_failed", problem.RootElement.GetProperty("code").GetString());
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
        using HttpClient client = CreateClient();

        // One user per authorized route: several probed operations share the
        // user-partitioned reauth dimension (5 per 15 min), so a single shared
        // user overflows it and the matrix would measure the limiter instead
        // of antiforgery.
        var authCookies = new Dictionary<string, string>();
        int routeIndex = 0;
        foreach ((string key, UnsafeRouteCase route) in UnsafeRoutes)
        {
            routeIndex++;
            if (!route.RequiresAuthorization)
            {
                continue;
            }

            UserId routeUser = await _factory.SeedUserAsync(
                admin: route.AuthorizationPolicy == AuthorizationPolicies.RequireAdmin,
                email: $"antiforgery-matrix-{routeIndex}@example.test",
                password: "current-password");
            authCookies[key] = await SignInAsync(client, routeUser);
        }

        IReadOnlyDictionary<string, RouteEndpoint> mapped = MappedUnsafeRoutes();
        Assert.Equal(UnsafeRoutes.Keys.Order(), mapped.Keys.Order());
        foreach ((string key, UnsafeRouteCase route) in UnsafeRoutes)
        {
            string?[] actualPolicies = mapped[key].Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .Select(metadata => metadata.Policy)
                .ToArray();
            string?[] expectedPolicies = route.AuthorizationPolicy is null
                ? []
                : [route.AuthorizationPolicy];
            Assert.Equal(expectedPolicies, actualPolicies);
        }

        SideEffectSnapshot baseline = await SnapshotAsync();
        foreach ((string key, UnsafeRouteCase route) in UnsafeRoutes)
        {
            using HttpRequestMessage request = CreateRequest(key, route.CreateBody());
            if (route.RequiresAuthorization)
            {
                request.Headers.Add(HeaderNames.Cookie, authCookies[key]);
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
            HttpResponseMessage response = await AntiforgeryTestClient.SendAsync(
                client,
                MethodFromKey(key),
                RouteFromKey(key),
                route.CreateBody());
            string body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("antiforgery_failed", body, StringComparison.Ordinal);
            if (route.RequiresAuthorization)
            {
                HttpStatusCode expectedStatus = route.AuthorizationPolicy ==
                    AuthorizationPolicies.RequireAdmin
                        ? HttpStatusCode.NotFound
                        : HttpStatusCode.Unauthorized;
                Assert.Equal(expectedStatus, response.StatusCode);
                if (expectedStatus == HttpStatusCode.Unauthorized)
                {
                    Assert.Contains("unauthorized", body, StringComparison.Ordinal);
                }
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
        new(MethodFromKey(key), RouteFromKey(key))
        {
            Content = body,
        };

    private static string RouteFromKey(string key) =>
        key[(key.IndexOf(' ') + 1)..]
            .Replace("{id:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{friendshipId}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{blockId}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{kind}", "password", StringComparison.Ordinal);

    private static HttpMethod MethodFromKey(string key) =>
        new(key[..key.IndexOf(' ')]);

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

    private static UnsafeRouteCase Public(Func<HttpContent?> body) => new(null, body);

    private static UnsafeRouteCase Authenticated(Func<HttpContent?> body) =>
        new(AuthorizationPolicies.RequireAuthenticated, body);

    private static UnsafeRouteCase Onboarded(Func<HttpContent?> body) =>
        new(AuthorizationPolicies.RequireOnboarded, body);

    private static UnsafeRouteCase Admin(Func<HttpContent?> body) =>
        new(AuthorizationPolicies.RequireAdmin, body);

    private sealed record UnsafeRouteCase(
        string? AuthorizationPolicy,
        Func<HttpContent?> CreateBody)
    {
        public bool RequiresAuthorization => AuthorizationPolicy is not null;
    }

    private sealed record SideEffectSnapshot(
        int DomainUsers,
        int AuthUsers,
        int Tokens,
        int Emails);
}
