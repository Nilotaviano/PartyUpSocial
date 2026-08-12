using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
            ["PUT /api/v1/me/email-notifications"] = Onboarded(() =>
                JsonContent.Create(new { enabled = false })),
            // TOUR_DESIGN.md §4: idempotent, RequireOnboarded (registered surface only).
            ["POST /api/v1/me/tour-completed"] = Onboarded(() => null),
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
            ["POST /api/v1/email-notifications/unsubscribe"] = Public(() =>
                JsonContent.Create(new { token = "invalid" })),
            ["POST /api/v1/friend-requests"] = Onboarded(() =>
                JsonContent.Create(new { targetUsername = "MatrixPlayer" })),
            ["POST /api/v1/friend-requests/{friendshipId}/accept"] = Onboarded(() => null),
            ["POST /api/v1/friend-requests/{friendshipId}/reject"] = Onboarded(() => null),
            ["DELETE /api/v1/friend-requests/{friendshipId}"] = Onboarded(() => null),
            ["DELETE /api/v1/friends/{friendshipId}"] = Onboarded(() => null),
            ["POST /api/v1/friends/imports/steam"] = Onboarded(() => null),
            ["POST /api/v1/blocks"] = Onboarded(() =>
                JsonContent.Create(new { targetUsername = "MatrixPlayer" })),
            ["DELETE /api/v1/blocks/{blockId}"] = Onboarded(() => null),
            ["POST /api/v1/admin/users/{id:guid}/ban"] = Admin(() =>
                JsonContent.Create(new { removeContent = false })),
            ["POST /api/v1/admin/users/{id:guid}/unban"] = Admin(() => null),
            ["POST /api/v1/admin/maintenance/deletion-sweep"] = Admin(() => null),
            ["POST /api/v1/sessions"] = Onboarded(() => JsonContent.Create(new
            {
                title = "Antiforgery probe",
                startTime = "2027-01-01T00:00:00Z",
                isPrivate = true,
            })),
            ["PATCH /api/v1/sessions/{sessionId:guid}"] = Onboarded(() =>
                JsonContent.Create(new { title = "Antiforgery probe", ifMatch = "probe" })),
            ["DELETE /api/v1/sessions/{sessionId:guid}"] = Onboarded(() => null),
            ["POST /api/v1/sessions/{sessionId:guid}/invites"] = Onboarded(() =>
                JsonContent.Create(new { userIds = Array.Empty<Guid>() })),
            ["POST /api/v1/sessions/{sessionId:guid}/guest-links"] = Onboarded(() =>
                JsonContent.Create(new { count = 1 })),
            ["DELETE /api/v1/sessions/{sessionId:guid}/guest-links/{guestLinkId:guid}"] =
                Onboarded(() => null),
            ["DELETE /api/v1/sessions/{sessionId:guid}/participations/{participationId:guid}/guest"] =
                Onboarded(() => null),
            ["PUT /api/v1/sessions/{sessionId:guid}/my-participation"] = Onboarded(() =>
                JsonContent.Create(new { response = "Going" })),
            ["DELETE /api/v1/sessions/{sessionId:guid}/my-participation"] = Onboarded(() => null),
            ["POST /api/v1/sessions/{sessionId:guid}/join-requests"] = Onboarded(() => null),
            ["DELETE /api/v1/sessions/{sessionId:guid}/join-requests/mine"] = Onboarded(() => null),
            ["POST /api/v1/sessions/{sessionId:guid}/join-requests/{requestId:guid}/approve"] =
                Onboarded(() => null),
            ["POST /api/v1/sessions/{sessionId:guid}/join-requests/{requestId:guid}/reject"] =
                Onboarded(() => null),
            ["POST /api/v1/sessions/{sessionId:guid}/participations/{participationId:guid}/promote"] =
                Onboarded(() => null),
            ["POST /api/v1/sessions/{sessionId:guid}/participations/{participationId:guid}/reject"] =
                Onboarded(() => null),
            ["PUT /api/v1/sessions/{sessionId:guid}/my-reminder"] = Onboarded(() =>
                JsonContent.Create(new { choice = "Off" })),
            // PHASE5_DESIGN.md §8: the unsafe /me/availability/** surface (round-2 fix, disclosed
            // extension — Phase 4 S4 precedent for mechanical RateLimitTests/AntiforgeryTests
            // inventory extensions).
            ["POST /api/v1/me/availability/blocks"] = Onboarded(() => JsonContent.Create(new
            {
                startTime = "2027-01-01T00:00:00Z",
                endTime = "2027-01-01T01:00:00Z",
            })),
            ["PATCH /api/v1/me/availability/blocks/{blockId:guid}"] = Onboarded(() => JsonContent.Create(new
            {
                startTime = "2027-01-01T00:00:00Z",
                endTime = "2027-01-01T01:00:00Z",
            })),
            ["DELETE /api/v1/me/availability/blocks/{blockId:guid}"] = Onboarded(() => null),
            ["POST /api/v1/me/availability/series"] = Onboarded(() => JsonContent.Create(new
            {
                dayOfWeek = 1,
                startTime = "18:00",
                durationMinutes = 60,
                effectiveFrom = "2027-01-04",
            })),
            ["DELETE /api/v1/me/availability/series/{seriesId:guid}"] = Onboarded(() => null),
            ["PATCH /api/v1/me/availability/series/{seriesId:guid}/occurrences/{occurrenceDate}"] =
                Onboarded(() => JsonContent.Create(new
                {
                    scope = "onlyThis",
                    startTime = "2027-01-01T00:00:00Z",
                    endTime = "2027-01-01T01:00:00Z",
                })),
            ["DELETE /api/v1/me/availability/series/{seriesId:guid}/occurrences/{occurrenceDate}"] =
                Onboarded(() => null),
            ["POST /api/v1/guest/{guestLinkId:guid}/claim"] = Public(() =>
                JsonContent.Create(new { displayName = "Probe Guest", response = "Pending" })),
            ["PUT /api/v1/guest/{guestLinkId:guid}/display-name"] = Public(() =>
                JsonContent.Create(new { displayName = "Probe Guest" })),
            ["PUT /api/v1/guest/{guestLinkId:guid}/participation"] = Public(() =>
                JsonContent.Create(new { response = "Going" })),
            // PHASE6_DESIGN.md §4 (ticket-P6-S4a): the registered poll HTTP surface.
            ["POST /api/v1/polls"] = Onboarded(() => JsonContent.Create(new
            {
                title = "Antiforgery probe",
                optionStartTimes = new[] { "2027-01-01T00:00:00Z", "2027-01-02T00:00:00Z" },
                isPrivate = true,
            })),
            ["POST /api/v1/polls/{pollId:guid}/options"] = Onboarded(() =>
                JsonContent.Create(new { startTime = "2027-01-01T00:00:00Z" })),
            ["DELETE /api/v1/polls/{pollId:guid}/options/{optionId:guid}"] = Onboarded(() => null),
            ["PUT /api/v1/polls/{pollId:guid}/options/{optionId:guid}/vote"] = Onboarded(() =>
                JsonContent.Create(new { choice = "available" })),
            ["POST /api/v1/polls/{pollId:guid}/finalize"] = Onboarded(() =>
                JsonContent.Create(new { optionId = Guid.Empty })),
            ["POST /api/v1/polls/{pollId:guid}/cancel"] = Onboarded(() => null),
            ["POST /api/v1/polls/{pollId:guid}/guest-links"] = Onboarded(() =>
                JsonContent.Create(new { count = 1 })),
            ["DELETE /api/v1/polls/{pollId:guid}/guest-links/{guestLinkId:guid}"] = Onboarded(() => null),
            ["POST /api/v1/polls/{pollId:guid}/guests/{guestIdentityId:guid}/remove"] = Onboarded(() => null),
            ["DELETE /api/v1/polls/{pollId:guid}/my-participation"] = Onboarded(() => null),
            ["POST /api/v1/polls/{pollId:guid}/join-requests"] = Onboarded(() => null),
            ["DELETE /api/v1/polls/{pollId:guid}/join-requests/mine"] = Onboarded(() => null),
            ["POST /api/v1/polls/{pollId:guid}/join-requests/{requestId:guid}/approve"] =
                Onboarded(() => null),
            ["POST /api/v1/polls/{pollId:guid}/join-requests/{requestId:guid}/reject"] =
                Onboarded(() => null),
            // PHASE6_DESIGN.md §4 (ticket-P6-S4b): the poll guest arm — the shipped guest session
            // routes' Public() pattern (no authorization, no onboarding).
            ["POST /api/v1/guest/{guestLinkId:guid}/poll/claim"] = Public(() =>
                JsonContent.Create(new { displayName = "Probe Guest" })),
            ["PUT /api/v1/guest/{guestLinkId:guid}/poll/options/{optionId:guid}/vote"] = Public(() =>
                JsonContent.Create(new { choice = "available" })),
            ["POST /api/v1/guest/{guestLinkId:guid}/poll/options"] = Public(() =>
                JsonContent.Create(new { startTime = "2027-01-01T00:00:00Z" })),
            ["PUT /api/v1/guest/{guestLinkId:guid}/poll/display-name"] = Public(() =>
                JsonContent.Create(new { displayName = "Probe Guest" })),
            // PHASE7_DESIGN.md §4 (ticket-P7-S4): the S4 HTTP surface's eight unsafe routes.
            ["POST /api/v1/push/subscriptions"] = Onboarded(() => JsonContent.Create(new
            {
                channel = "webpush",
                endpoint = "https://push.example.test/probe",
                publicKey = "probe-public-key",
                authSecret = "probe-auth-secret",
            })),
            ["DELETE /api/v1/push/subscriptions/{subscriptionId:guid}"] = Onboarded(() => null),
            ["POST /api/v1/notifications/{notificationId:guid}/read"] = Onboarded(() => null),
            ["POST /api/v1/notifications/read-all"] = Onboarded(() => null),
            ["PUT /api/v1/sessions/{sessionId:guid}/mute"] = Onboarded(() => null),
            ["DELETE /api/v1/sessions/{sessionId:guid}/mute"] = Onboarded(() => null),
            ["PUT /api/v1/polls/{pollId:guid}/mute"] = Onboarded(() => null),
            ["DELETE /api/v1/polls/{pollId:guid}/mute"] = Onboarded(() => null),
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
        // round-2 fix: the "default" dimension chains onto every request regardless of operation
        // (CoversEveryApiOperation: true, RateLimitPolicies.cs) — this matrix's setup-plus-two-loop
        // shape already made ~4 HTTP calls per route against a single shared IP partition before the
        // Phase 5 additions, and 69 routes push that past the 300/IP/minute default ceiling. Each
        // route's whole sequence (sign-in, both loops) is pinned to its own synthetic
        // X-Forwarded-For value instead, spreading the "default" partition across many buckets — the
        // antiforgery/authorization behavior under test is unaffected by which IP a request claims.
        await using Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> forwardedFactory =
            ForwardedHeadersFactory();
        using HttpClient client = CreateClient(forwardedFactory);

        var forwardedIps = new Dictionary<string, string>(StringComparer.Ordinal);
        int ipIndex = 0;
        foreach (string key in UnsafeRoutes.Keys)
        {
            ipIndex++;
            forwardedIps[key] = $"198.51.100.{ipIndex}";
        }

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
            authCookies[key] = await SignInAsync(client, routeUser, forwardedIps[key]);
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
            request.Headers.Add("X-Forwarded-For", forwardedIps[key]);
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
                route.CreateBody(),
                forwardedFor: forwardedIps[key]);
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

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> ForwardedHeadersFactory() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Network:TrustedProxies:0"] = "10.0.0.2",
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(
                    IPAddress.Parse("10.0.0.2"))));
        });

    private sealed class RemoteIpStartupFilter(IPAddress proxyAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use(async (context, following) =>
                {
                    context.Connection.RemoteIpAddress = proxyAddress;
                    await following(context);
                });
                next(application);
            };
    }

    private HttpClient CreateClient() => CreateClient(_factory);

    private static HttpClient CreateClient(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) => factory.CreateClient(
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
            .Replace("{blockId:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{blockId}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{seriesId:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{occurrenceDate}", "2027-01-04", StringComparison.Ordinal)
            .Replace("{kind}", "password", StringComparison.Ordinal)
            .Replace("{sessionId:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{guestLinkId:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{participationId:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{requestId:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{pollId:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{optionId:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{guestIdentityId:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{subscriptionId:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace("{notificationId:guid}", Guid.Empty.ToString("D"), StringComparison.Ordinal);

    private static HttpMethod MethodFromKey(string key) =>
        new(key[..key.IndexOf(' ')]);

    private static async Task<string> SignInAsync(HttpClient client, UserId userId, string? forwardedFor = null)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null,
            forwardedFor: forwardedFor);
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
