using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PartyUp.Application.Abstractions;
using PartyUp.Api.Auth;
using PartyUp.Api.Endpoints;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SecurityStampTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Response_ForAValidSession_CarriesNoValidatorIssuedSetCookie()
    {
        UserId userId = await _factory.SeedUserAsync();
        using HttpClient client = CreateClient();
        string cookie = await SignInAsync(client, userId);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/_slice2/authenticated");
        request.Headers.Add(HeaderNames.Cookie, cookie);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
    }

    [Fact]
    public async Task Validator_ReadsItsContextPrincipal_AndSeedsTheRequestProjectionCacheOnce()
    {
        UserId userId = UserId.New();
        const string SecurityStamp = "matching-stamp";
        var reader = new RecordingProjectionReader(new CurrentUserProjection(
            userId,
            UserLifecycle.Active,
            IsBanned: false,
            GraceEndsAt: null,
            IsAdmin: false,
            IsProfileComplete: true,
            Locale.Create("en"),
            SecurityStamp));
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICurrentUserProjectionReader>();
                services.AddSingleton<ICurrentUserProjectionReader>(reader);
            }));
        using IServiceScope scope = factory.Services.CreateScope();
        var cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var identityOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<IdentityOptions>>()
            .Value;
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme);
        identity.AddClaim(new Claim(
            identityOptions.ClaimsIdentity.UserIdClaimType,
            userId.Value.ToString("D")));
        identity.AddClaim(new Claim(
            identityOptions.ClaimsIdentity.SecurityStampClaimType,
            SecurityStamp));
        string protectedTicket = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
            },
            IdentityConstants.ApplicationScheme));
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false,
            });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/_slice2/authenticated");
        request.Headers.Add(HeaderNames.Cookie, $"pu.auth={protectedTicket}");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(userId, reader.RequestedUserId);
    }

    [Fact]
    public async Task Response_ForAPrincipalWithNoProjectionRow_IsRejected()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var identityOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<IdentityOptions>>()
            .Value;
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme);
        identity.AddClaim(new Claim(
            identityOptions.ClaimsIdentity.UserIdClaimType,
            Guid.NewGuid().ToString("D")));
        identity.AddClaim(new Claim(
            identityOptions.ClaimsIdentity.SecurityStampClaimType,
            Guid.NewGuid().ToString("N")));
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
            },
            IdentityConstants.ApplicationScheme);
        string protectedTicket = cookieOptions.TicketDataFormat.Protect(ticket);
        using HttpClient client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/_slice2/authenticated");
        request.Headers.Add(HeaderNames.Cookie, $"pu.auth={protectedTicket}");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues(HeaderNames.SetCookie),
            value => value.StartsWith("pu.auth=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Session_AfterARejectedPrincipal_IsAnonymous()
    {
        UserId userId = UserId.New();
        var reader = new RecordingProjectionReader(new CurrentUserProjection(
            userId,
            UserLifecycle.Active,
            IsBanned: false,
            GraceEndsAt: null,
            IsAdmin: false,
            IsProfileComplete: true,
            Locale.Create("en"),
            SecurityStamp: "current-stamp"));
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICurrentUserProjectionReader>();
                services.AddSingleton<ICurrentUserProjectionReader>(reader);
            }));
        using IServiceScope scope = factory.Services.CreateScope();
        var cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var identityOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<IdentityOptions>>()
            .Value;
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme);
        identity.AddClaim(new Claim(
            identityOptions.ClaimsIdentity.UserIdClaimType,
            userId.Value.ToString("D")));
        identity.AddClaim(new Claim(
            identityOptions.ClaimsIdentity.SecurityStampClaimType,
            "stale-stamp"));
        string protectedTicket = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
            },
            IdentityConstants.ApplicationScheme));
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false,
            });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        request.Headers.Add(HeaderNames.Cookie, $"pu.auth={protectedTicket}");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SessionResponse session = (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
        Assert.False(session.Authenticated);
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task MissingProjection_FailsClosedForEveryAuthorizationPolicy()
    {
        var executions = new HandlerExecutionProbe();
        await using WebApplication application = await CreatePolicyProbeAsync(executions);
        using IServiceScope scope = application.Services.CreateScope();
        CookieAuthenticationOptions cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        IdentityOptions identityOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<IdentityOptions>>()
            .Value;
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme);
        identity.AddClaim(new Claim(
            identityOptions.ClaimsIdentity.UserIdClaimType,
            Guid.NewGuid().ToString("D")));
        identity.AddClaim(new Claim(
            identityOptions.ClaimsIdentity.SecurityStampClaimType,
            "validly-protected-but-missing-subject"));
        string ticket = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
                IsPersistent = true,
            },
            IdentityConstants.ApplicationScheme));
        using HttpClient client = application.GetTestClient();

        (string Path, HttpStatusCode Status)[] probes =
        [
            ("/authenticated", HttpStatusCode.Unauthorized),
            ("/onboarded", HttpStatusCode.Unauthorized),
            ("/admin", HttpStatusCode.NotFound),
        ];
        foreach ((string path, HttpStatusCode status) in probes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(HeaderNames.Cookie, $"pu.auth={ticket}");
            HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(status, response.StatusCode);
            Assert.Contains(
                response.Headers.GetValues(HeaderNames.SetCookie),
                value => value.StartsWith("pu.auth=;", StringComparison.Ordinal));
        }

        Assert.Equal(0, executions.Count);
    }

    private HttpClient CreateClient() => _factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        return response.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private static async Task<WebApplication> CreatePolicyProbeAsync(
        HandlerExecutionProbe executions)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<NodaTime.IClock>(
            new MutableClock(NodaTime.Instant.FromUtc(2026, 7, 31, 12, 0)));
        builder.Services.AddSingleton<ICurrentUserProjectionReader, NullProjectionReader>();
        builder.Services.AddScoped<CurrentUserAccessor>();
        builder.Services.AddScoped<ICurrentUserAccessor>(provider =>
            provider.GetRequiredService<CurrentUserAccessor>());
        builder.Services.AddPartyUpCookieAuthentication(builder.Environment);
        builder.Services.AddPartyUpAuthorization();
        WebApplication application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapGet(
                "/authenticated",
                () => executions.Executed())
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated);
        application.MapGet(
                "/onboarded",
                () => executions.Executed())
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);
        application.MapGet(
                "/admin",
                () => executions.Executed())
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        await application.StartAsync();
        return application;
    }

    private sealed class RecordingProjectionReader(CurrentUserProjection projection)
        : ICurrentUserProjectionReader
    {
        public int ReadCount { get; private set; }

        public UserId? RequestedUserId { get; private set; }

        public Task<CurrentUserProjection?> ReadAsync(
            UserId id,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            RequestedUserId = id;
            return Task.FromResult<CurrentUserProjection?>(projection);
        }
    }

    private sealed class NullProjectionReader : ICurrentUserProjectionReader
    {
        public Task<CurrentUserProjection?> ReadAsync(
            UserId id,
            CancellationToken cancellationToken) =>
            Task.FromResult<CurrentUserProjection?>(null);
    }

    private sealed class HandlerExecutionProbe
    {
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        internal IResult Executed()
        {
            Interlocked.Increment(ref _count);
            return Results.NoContent();
        }
    }
}
