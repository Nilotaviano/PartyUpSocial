using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Auth;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ExternalIntentTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new(googleEnabled: true);

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("google", "login")]
    [InlineData("google", "link")]
    [InlineData("google", "reauth")]
    [InlineData("steam", "login")]
    [InlineData("steam", "link")]
    [InlineData("steam", "reauth")]
    public async Task Start_WithoutAnIntent_IsRejected_InEveryMode(string provider, string mode)
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/auth/external/{provider}/start?mode={mode}");

        await AssertIntentRequiredAsync(response);
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example/path")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/\\evil.example")]
    public async Task Intent_HostileReturnToIsSanitizedBeforeTokenIssuance(string returnTo)
    {
        var probe = new TokenIssueProbe();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityTokenService>();
                services.AddScoped<SecurityTokenService>();
                services.AddScoped<ISecurityTokenService>(provider =>
                    new CapturingTokenService(
                        provider.GetRequiredService<SecurityTokenService>(),
                        probe));
            }));
        using HttpClient client = Slice5HttpTest.CreateClient(factory);

        await MintAsync(client, returnTo: returnTo);

        Assert.Equal("/app", probe.Single().Intent!.ReturnTo);
    }

    [Fact]
    public async Task Complete_WithAMismatchedIntentMode_LinksNothing_D087()
    {
        UserId userId = await _factory.SeedUserAsync();
        await AddGoogleLoginAsync(userId, "already-linked-key");
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var useCase = scope.ServiceProvider.GetRequiredService<CompleteExternalSignInUseCase>();

        ExternalSignInResult login = await useCase.ExecuteAsync(
            new CompleteExternalSignInCommand(
                ExternalProviderPolicy.Google,
                "already-linked-key",
                "player@example.test",
                true,
                ExternalStartMode.Login,
                true,
                null,
                userId),
            default);
        ExternalSignInResult malformedLink = await useCase.ExecuteAsync(
            new CompleteExternalSignInCommand(
                ExternalProviderPolicy.Google,
                "attacker-key",
                "attacker@example.test",
                true,
                ExternalStartMode.Link,
                true,
                null,
                userId),
            default);

        Assert.True(login.Succeeded);
        Assert.Equal("intent_mode_mismatch", malformedLink.Code);
        Assert.Equal(1, await LoginCountAsync(userId));
    }

    [Fact]
    public async Task Start_ConsumesTheIntentOnce()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        MintedIntent minted = await MintAsync(client);

        HttpResponseMessage first = await StartAsync(client, minted);
        HttpResponseMessage second = await StartAsync(client, minted);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        await AssertIntentRequiredAsync(second);
    }

    [Fact]
    public async Task Start_IgnoresQueryStringModeReturnToAndAttestation()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        MintedIntent minted = await MintAsync(client, returnTo: "/settings/profile");

        HttpResponseMessage response = await StartAsync(
            client,
            minted,
            "&mode=link&returnTo=//evil.test&ageGateAttested=false");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https", response.Headers.Location?.Scheme);
        Assert.Equal("accounts.google.com", response.Headers.Location?.Host);
    }

    [Fact]
    public async Task Start_WithAnIntentMintedInAnotherBrowser_IsRejected()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        MintedIntent minted = await MintAsync(client);

        HttpResponseMessage refused = await StartAsync(client, minted with { Cookie = null });
        HttpResponseMessage retry = await StartAsync(client, minted);

        await AssertIntentRequiredAsync(refused);
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
    }

    [Fact]
    public async Task Start_RemovesOnlyTheUsedBinderFromTheIntentCookie()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        MintedIntent first = await MintAsync(client);
        MintedIntent second = await MintAsync(client, existingCookie: first.Cookie);

        HttpResponseMessage firstStart = await StartAsync(
            client,
            first with { Cookie = second.Cookie });
        string remainingCookie = ReadCookie(firstStart, "pu.intent");
        HttpResponseMessage secondStart = await StartAsync(
            client,
            second with { Cookie = remainingCookie });

        Assert.Equal(HttpStatusCode.Redirect, firstStart.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, secondStart.StatusCode);
        Assert.Contains(
            secondStart.Headers.GetValues(HeaderNames.SetCookie),
            value => value.StartsWith("pu.intent=", StringComparison.Ordinal) &&
                     value.Contains("max-age=0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Start_WithTwoConcurrentlyMintedIntents_BothSucceed()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        MintedIntent first = await MintAsync(client);
        MintedIntent second = await MintAsync(client, existingCookie: first.Cookie);

        HttpResponseMessage firstStart = await StartAsync(
            client,
            first with { Cookie = second.Cookie });
        string remainingCookie = ReadCookie(firstStart, "pu.intent");
        HttpResponseMessage secondStart = await StartAsync(
            client,
            second with { Cookie = remainingCookie });

        Assert.Equal(HttpStatusCode.Redirect, firstStart.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, secondStart.StatusCode);
    }

    [Fact]
    public async Task Start_WithAnInjectedIntentCookieValue_IsRejected()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        MintedIntent minted = await MintAsync(client);

        HttpResponseMessage refused = await StartAsync(
            client,
            minted with { Cookie = "pu.intent=caller-chosen" });
        HttpResponseMessage retry = await StartAsync(client, minted);

        await AssertIntentRequiredAsync(refused);
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
    }

    private async Task AddGoogleLoginAsync(UserId userId, string providerKey)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        AuthUser user = (await users.FindByIdAsync(userId.Value.ToString("D")))!;
        Assert.True((await users.AddLoginAsync(
            user,
            new UserLoginInfo("google", providerKey, "google"))).Succeeded);
    }

    private async Task<int> LoginCountAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        AuthUser user = (await users.FindByIdAsync(userId.Value.ToString("D")))!;
        return (await users.GetLoginsAsync(user)).Count;
    }

    private static async Task<MintedIntent> MintAsync(
        HttpClient client,
        string? existingCookie = null,
        string returnTo = "/app")
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/external/intent",
            JsonContent.Create(new
            {
                provider = "google",
                mode = "login",
                returnTo,
                ageGateAttested = true,
            }),
            existingCookie);
        response.EnsureSuccessStatusCode();
        ExternalIntentResponse body =
            (await response.Content.ReadFromJsonAsync<ExternalIntentResponse>())!;
        return new MintedIntent(body.Intent, ReadCookie(response, "pu.intent"));
    }

    private static async Task<HttpResponseMessage> StartAsync(
        HttpClient client,
        MintedIntent minted,
        string extraQuery = "")
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/auth/external/google/start?intent={Uri.EscapeDataString(minted.Intent)}{extraQuery}");
        if (minted.Cookie is not null)
        {
            request.Headers.Add(HeaderNames.Cookie, minted.Cookie);
        }

        return await client.SendAsync(request);
    }

    private static string ReadCookie(HttpResponseMessage response, string name)
    {
        SetCookieHeaderValue cookie = response.Headers
            .GetValues(HeaderNames.SetCookie)
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(value => value.Name.Value == name);
        return $"{cookie.Name.Value}={cookie.Value.Value}";
    }

    private static async Task AssertIntentRequiredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("intent_required", problem.Code);
        Assert.Null(response.Headers.Location);
    }

    private sealed class TokenIssueProbe
    {
        private readonly object _gate = new();
        private IssueTokenRequest? _issue;

        internal void Record(IssueTokenRequest issue)
        {
            lock (_gate)
            {
                if (_issue is not null)
                {
                    throw new InvalidOperationException("Expected exactly one issued token.");
                }

                _issue = issue;
            }
        }

        internal IssueTokenRequest Single()
        {
            lock (_gate)
            {
                return _issue ?? throw new InvalidOperationException("No token was issued.");
            }
        }
    }

    private sealed class CapturingTokenService(
        SecurityTokenService inner,
        TokenIssueProbe probe) : ISecurityTokenService
    {
        public Task<IssuedToken> IssueAsync(
            IssueTokenRequest request,
            CancellationToken cancellationToken)
        {
            probe.Record(request);
            return inner.IssueAsync(request, cancellationToken);
        }

        public Task<TokenRedemption> RedeemAsync(
            RedeemTokenRequest request,
            CancellationToken cancellationToken) =>
            inner.RedeemAsync(request, cancellationToken);
    }

    private sealed record MintedIntent(string Intent, string? Cookie);
}
