using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Auth;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class PublicDeletionRouteTests : IAsyncLifetime, IDisposable
{
    private const string SteamId = "76561198000000000";
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Request_ForAKnownAndAnUnknownAddress_AreIndistinguishable_D072()
    {
        const string knownAddress = "KnownCase@Example.test";
        await _factory.SeedUserAsync(email: knownAddress);
        var probe = new TokenIssueProbe();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityTokenService>();
                services.AddScoped<ISecurityTokenService>(provider =>
                    new ProbingTokenService(
                        provider.GetRequiredService<SecurityTokenService>(),
                        probe));
            }));
        using HttpClient client = Slice5HttpTest.CreateClient(factory);

        HttpResponseMessage known = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/account-deletion-requests",
            JsonContent.Create(new { email = knownAddress }));
        HttpResponseMessage unknown = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/account-deletion-requests",
            JsonContent.Create(new { email = "unknown@example.test" }));

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(
            await known.Content.ReadAsStringAsync(),
            await unknown.Content.ReadAsStringAsync());
        Assert.True(Slice5HttpTest.HeaderNames(known).SetEquals(Slice5HttpTest.HeaderNames(unknown)));
        IReadOnlyList<EmailMessage> messages = await _factory.EmailSender.WaitForCountAsync(
            message => message.Template is
                EmailTemplate.DeletionRequestConfirmation or EmailTemplate.DeletionRequestNoAccount,
            2);
        Assert.Contains(messages, message =>
            message.Template == EmailTemplate.DeletionRequestConfirmation &&
            message.ToAddress == knownAddress);
        Assert.Contains(messages, message =>
            message.Template == EmailTemplate.DeletionRequestNoAccount &&
            message.ToAddress == "unknown@example.test");
        Assert.Equal(2, probe.Issues);
    }

    [Fact]
    public async Task RequestThenConfirm_StartsTheGrace_D072()
    {
        const string address = "confirm-deletion@example.test";
        UserId userId = await _factory.SeedUserAsync(email: address);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/account-deletion-requests",
            JsonContent.Create(new { email = address }));
        EmailMessage message = await _factory.EmailSender.WaitForAsync(value =>
            value.Template == EmailTemplate.DeletionRequestConfirmation &&
            value.ToAddress == address);

        HttpResponseMessage confirmed = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/account-deletion-requests/confirm",
            JsonContent.Create(new { token = message.Model["Token"] }));

        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        Assert.True((await ReadUserAsync(userId)).IsPendingDeletion);
    }

    [Fact]
    public async Task SteamAssertion_ForAnEmaillessBannedAccount_MintsAConfirmationTokenWithoutStartingTheGrace_D072()
    {
        using SteamLegResult result = await RunSteamLegAsync(banned: true);

        Assert.Contains("#token=", result.Callback.Headers.Location!.OriginalString, StringComparison.Ordinal);
        User user = await ReadUserAsync(result.UserId);
        Assert.True(user.IsActive);
        Assert.True(user.IsBanned);
    }

    [Fact]
    public async Task SteamAssertion_ThenConfirm_StartsTheGrace_D072()
    {
        using SteamLegResult result = await RunSteamLegAsync(banned: false);
        string[] locationParts = result.Callback.Headers.Location!.OriginalString.Split('#', 2);
        Assert.Equal(2, locationParts.Length);
        Assert.StartsWith("token=", locationParts[1], StringComparison.Ordinal);
        string token = Uri.UnescapeDataString(locationParts[1]["token=".Length..]);

        HttpResponseMessage confirmed = await AntiforgeryTestClient.PostAsync(
            result.Client,
            "/api/v1/account-deletion-requests/confirm",
            JsonContent.Create(new { token }));

        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        Assert.True((await ReadUserAsync(result.UserId)).IsPendingDeletion);
    }

    [Fact]
    public async Task SteamAssertion_IssuesNoCookie_D180()
    {
        using SteamLegResult result = await RunSteamLegAsync(banned: false);

        Assert.DoesNotContain(
            result.Callback.Headers.GetValues(HeaderNames.SetCookie),
            value => value.StartsWith("pu.auth=", StringComparison.Ordinal));
    }

    private async Task<SteamLegResult> RunSteamLegAsync(bool banned)
    {
        UserId userId = await _factory.SeedUserAsync(
            banned: banned,
            email: null);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAccountGateway>()
                .AddLoginAsync(userId, ExternalProviderPolicy.Steam, SteamId, default);
        }

        var verifier = new AcceptingSteamVerifier(SteamId);
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamOpenIdVerifier>();
                services.AddSingleton<ISteamOpenIdVerifier>(verifier);
            }));
        HttpClient client = Slice5HttpTest.CreateClient(factory);
        HttpResponseMessage start = await client.GetAsync(
            "/api/v1/account-deletion-requests/steam/start");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        string stateCookie = Cookie(start, "pu.extdel");
        string returnTo = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["openid.return_to"]!;
        string nonce = QueryHelpers.ParseQuery(new Uri(returnTo).Query)["n"]!;
        using var callbackRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/account-deletion-requests/steam/callback?n={Uri.EscapeDataString(nonce)}");
        callbackRequest.Headers.Add(HeaderNames.Cookie, stateCookie);
        HttpResponseMessage callback = await client.SendAsync(callbackRequest);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(1, verifier.Calls);
        return new SteamLegResult(userId, factory, client, callback);
    }

    private async Task<User> ReadUserAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .FindAsync(userId, default))!;
    }

    private static string Cookie(HttpResponseMessage response, string name)
    {
        SetCookieHeaderValue cookie = response.Headers.GetValues(HeaderNames.SetCookie)
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(value => value.Name.Value == name);
        return $"{cookie.Name.Value}={cookie.Value.Value}";
    }

    private sealed class AcceptingSteamVerifier(string steamId) : ISteamOpenIdVerifier
    {
        public int Calls { get; private set; }

        public Task<SteamOpenIdVerification> VerifyAsync(
            IReadOnlyDictionary<string, string> assertion,
            string expectedReturnTo,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SteamOpenIdVerification(true, steamId));
        }
    }

    private sealed class TokenIssueProbe
    {
        private int _issues;
        public int Issues => Volatile.Read(ref _issues);
        public void Record() => Interlocked.Increment(ref _issues);
    }

    private sealed class ProbingTokenService(
        SecurityTokenService inner,
        TokenIssueProbe probe) : ISecurityTokenService
    {
        public Task<IssuedToken> IssueAsync(
            IssueTokenRequest request,
            CancellationToken cancellationToken)
        {
            probe.Record();
            return inner.IssueAsync(request, cancellationToken);
        }

        public Task<TokenRedemption> RedeemAsync(
            RedeemTokenRequest request,
            CancellationToken cancellationToken) =>
            inner.RedeemAsync(request, cancellationToken);
    }

    private sealed class SteamLegResult(
        UserId userId,
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        HttpClient client,
        HttpResponseMessage callback) : IDisposable
    {
        public UserId UserId { get; } = userId;
        public HttpClient Client { get; } = client;
        public HttpResponseMessage Callback { get; } = callback;

        public void Dispose()
        {
            Callback.Dispose();
            Client.Dispose();
            factory.Dispose();
        }
    }
}
