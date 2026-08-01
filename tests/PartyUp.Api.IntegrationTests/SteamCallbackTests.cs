using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SteamCallbackTests
{
    private const string SteamId = "76561198000000000";
    private const string ReturnTo = "https://partyup.social/api/v1/auth/external/steam/callback?n=nonce";

    [Fact]
    public async Task Callback_WithAForgedAssertion_IsRejected_D067()
    {
        var verifier = CreateVerifier("is_valid:false");

        SteamOpenIdVerification result = await verifier.VerifyAsync(Assertion(), ReturnTo, default);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Callback_WithClaimedIdStrippedFromOpenIdSigned_IsRejected_D067()
    {
        var handler = new StubHandler("is_valid:true");
        var verifier = new SteamOpenIdVerifier(new HttpClient(handler));
        Dictionary<string, string> assertion = Assertion();
        assertion["openid.signed"] = "return_to,response_nonce,assoc_handle,op_endpoint";

        SteamOpenIdVerification result = await verifier.VerifyAsync(assertion, ReturnTo, default);

        Assert.False(result.Succeeded);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Callback_WithAWrongOpenIdMode_IsRejected()
    {
        var verifier = CreateVerifier("is_valid:true");
        Dictionary<string, string> assertion = Assertion();
        assertion["openid.mode"] = "checkid_setup";

        SteamOpenIdVerification result = await verifier.VerifyAsync(assertion, ReturnTo, default);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Callback_WithAMismatchedReturnTo_IsRejected()
    {
        var verifier = CreateVerifier("is_valid:true");

        SteamOpenIdVerification result = await verifier.VerifyAsync(
            Assertion(),
            ReturnTo + "-other",
            default);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Callback_ReplayedResponseNonce_IsRejected_D067()
    {
        var verifier = CreateVerifier("is_valid:true");

        SteamOpenIdVerification first = await verifier.VerifyAsync(Assertion(), ReturnTo, default);
        SteamOpenIdVerification replay = await verifier.VerifyAsync(Assertion(), ReturnTo, default);

        Assert.True(first.Succeeded);
        Assert.False(replay.Succeeded);
    }

    private static SteamOpenIdVerifier CreateVerifier(string response) =>
        new(new HttpClient(new StubHandler(response)));

    private static Dictionary<string, string> Assertion()
    {
        string claimedId = $"https://steamcommunity.com/openid/id/{SteamId}";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["openid.ns"] = SteamOpenIdVerifier.OpenIdNamespace,
            ["openid.mode"] = "id_res",
            ["openid.op_endpoint"] = SteamOpenIdVerifier.ProviderEndpoint,
            ["openid.claimed_id"] = claimedId,
            ["openid.identity"] = claimedId,
            ["openid.return_to"] = ReturnTo,
            ["openid.response_nonce"] = "2026-07-31T12:00:00Znonce",
            ["openid.assoc_handle"] = "handle",
            ["openid.signed"] = "claimed_id,identity,return_to,response_nonce,assoc_handle,op_endpoint",
            ["openid.sig"] = "signature",
        };
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            });
        }
    }
}

[Trait("Category", "Integration")]
public sealed class SteamCallbackRouteTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Start_WithOnlyPublicBaseUrlConfigured_UsesOneOriginForRealmAndReturnTo()
    {
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PublicBaseUrl"] = "https://signin.example.test/app",
                    ["Steam:Realm"] = string.Empty,
                })));
        using HttpClient client = Slice5HttpTest.CreateClient(factory);

        StartedSteamFlow flow = await StartSteamAsync(client);
        var query = QueryHelpers.ParseQuery(flow.ProviderRedirect.Query);
        string realm = query["openid.realm"].ToString();
        string returnTo = query["openid.return_to"].ToString();

        Assert.Equal("https://signin.example.test", realm);
        Assert.Equal(
            new Uri(realm).GetLeftPart(UriPartial.Authority),
            new Uri(returnTo).GetLeftPart(UriPartial.Authority));
        Assert.True(flow.StateSetCookie.HttpOnly);
        Assert.Equal("/api/v1/auth/external/steam", flow.StateSetCookie.Path.Value);
    }

    [Fact]
    public async Task Callback_WithAForgedAssertion_IsRejectedWithoutSessionOrAccount()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        StartedSteamFlow flow = await StartSteamAsync(client);

        HttpResponseMessage response = await SendCallbackAsync(client, flow);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("steam_assertion_invalid", problem.Code);
        AssertNoAuthCookie(response);
        AssertStateCookieDeleted(response);
        await AssertNoAccountAsync();
    }

    [Fact]
    public async Task Callback_InvokesTheRegisteredVerifierOnceAndHonorsANegativeVerdict()
    {
        var verifier = new RecordingVerifier();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamOpenIdVerifier>();
                services.AddSingleton<ISteamOpenIdVerifier>(verifier);
            }));
        using HttpClient client = Slice5HttpTest.CreateClient(factory);
        StartedSteamFlow flow = await StartSteamAsync(client);

        HttpResponseMessage response = await SendCallbackAsync(
            client,
            flow,
            "&openid.mode=id_res");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, verifier.Calls);
        AssertNoAuthCookie(response);
        await AssertNoAccountAsync();
    }

    [Theory]
    [InlineData("/u/Player.Name", "/u/player.name", true)]
    [InlineData("/add/2345-6789-AB", "/add/23456789AB", true)]
    [InlineData("/u/Player.Name", "/u/player.name", false)]
    [InlineData("/add/2345-6789-AB", "/add/23456789AB", false)]
    public async Task Callback_PreservesCanonicalFriendContinuationForEveryAccountState(
        string requested,
        string expected,
        bool completeProfile)
    {
        string steamId = $"7656119{Random.Shared.Next(100000000, 999999999)}";
        UserId userId = await _factory.SeedUserAsync(
            completeProfile: completeProfile,
            email: null);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAccountGateway>()
                .AddLoginAsync(
                    userId,
                    ExternalProviderPolicy.Steam,
                    steamId,
                    default);
        }

        var verifier = new AcceptingVerifier(steamId);
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamOpenIdVerifier>();
                services.AddSingleton<ISteamOpenIdVerifier>(verifier);
                services.RemoveAll<ISteamProfileClient>();
                services.AddSingleton<ISteamProfileClient>(new StaticSteamProfileClient());
            }));
        using HttpClient client = Slice5HttpTest.CreateClient(factory);
        StartedSteamFlow flow = await StartSteamAsync(client, requested);

        HttpResponseMessage response = await SendCallbackAsync(client, flow);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        string next = QueryHelpers.ParseQuery(
            new Uri(new Uri("https://localhost"), response.Headers.Location!).Query)["next"]!;
        Assert.Equal(expected, next);
        Assert.Equal(1, verifier.Calls);
    }

    private async Task AssertNoAccountAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await context.AuthUsers.ToListAsync());
        Assert.Empty(await context.DomainUsers.ToListAsync());
    }

    private static async Task<StartedSteamFlow> StartSteamAsync(
        HttpClient client,
        string returnTo = "/app")
    {
        HttpResponseMessage mint = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/external/intent",
            JsonContent.Create(new
            {
                provider = "steam",
                mode = "login",
                returnTo,
                ageGateAttested = true,
            }));
        mint.EnsureSuccessStatusCode();
        ExternalIntentResponse body =
            (await mint.Content.ReadFromJsonAsync<ExternalIntentResponse>())!;
        string intentCookie = ReadCookiePair(mint, "pu.intent");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/auth/external/steam/start?intent={Uri.EscapeDataString(body.Intent)}");
        request.Headers.Add(HeaderNames.Cookie, intentCookie);
        HttpResponseMessage start = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Uri providerRedirect = start.Headers.Location!;
        SetCookieHeaderValue stateSetCookie = ReadSetCookie(start, "pu.ext");
        string callbackReturnTo = QueryHelpers.ParseQuery(providerRedirect.Query)["openid.return_to"].ToString();
        string nonce = QueryHelpers.ParseQuery(new Uri(callbackReturnTo).Query)["n"].ToString();
        return new StartedSteamFlow(
            $"{stateSetCookie.Name.Value}={stateSetCookie.Value.Value}",
            nonce,
            providerRedirect,
            stateSetCookie);
    }

    private static async Task<HttpResponseMessage> SendCallbackAsync(
        HttpClient client,
        StartedSteamFlow flow,
        string extraQuery = "")
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/auth/external/steam/callback?n={Uri.EscapeDataString(flow.Nonce)}{extraQuery}");
        request.Headers.Add(HeaderNames.Cookie, flow.StateCookie);
        return await client.SendAsync(request);
    }

    private static SetCookieHeaderValue ReadSetCookie(
        HttpResponseMessage response,
        string name) =>
        response.Headers.GetValues(HeaderNames.SetCookie)
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(cookie => cookie.Name.Value == name);

    private static string ReadCookiePair(HttpResponseMessage response, string name)
    {
        SetCookieHeaderValue cookie = ReadSetCookie(response, name);
        return $"{cookie.Name.Value}={cookie.Value.Value}";
    }

    private static void AssertStateCookieDeleted(HttpResponseMessage response)
    {
        SetCookieHeaderValue cookie = ReadSetCookie(response, "pu.ext");
        Assert.Equal("/api/v1/auth/external/steam", cookie.Path.Value);
        Assert.Equal(DateTimeOffset.UnixEpoch, cookie.Expires);
    }

    private static void AssertNoAuthCookie(HttpResponseMessage response)
    {
        Assert.DoesNotContain(
            response.Headers.GetValues(HeaderNames.SetCookie),
            value => value.StartsWith("pu.auth=", StringComparison.Ordinal));
    }

    private sealed class RecordingVerifier : ISteamOpenIdVerifier
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<SteamOpenIdVerification> VerifyAsync(
            IReadOnlyDictionary<string, string> assertion,
            string expectedReturnTo,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(SteamOpenIdVerification.Invalid);
        }
    }

    private sealed class AcceptingVerifier(string steamId) : ISteamOpenIdVerifier
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<SteamOpenIdVerification> VerifyAsync(
            IReadOnlyDictionary<string, string> assertion,
            string expectedReturnTo,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new SteamOpenIdVerification(true, steamId));
        }
    }

    private sealed class StaticSteamProfileClient : ISteamProfileClient
    {
        public Task<SteamProfile?> GetProfileAsync(
            string steamId64,
            CancellationToken cancellationToken) =>
            Task.FromResult<SteamProfile?>(new SteamProfile("Steam Player", null));
    }

    private sealed record StartedSteamFlow(
        string StateCookie,
        string Nonce,
        Uri ProviderRedirect,
        SetCookieHeaderValue StateSetCookie);
}
