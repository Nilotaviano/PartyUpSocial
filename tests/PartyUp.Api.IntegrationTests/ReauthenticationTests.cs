using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ReauthenticationTests : IAsyncLifetime, IDisposable
{
    private const string Password = "correct-password";
    private readonly PartyUpApiFactory _factory = new(googleEnabled: true);

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Reauth_SharesTheLoginLockoutCounters_D067()
    {
        const string email = "shared-lockout@example.test";
        UserId userId = await _factory.SeedUserAsync(email: email, password: Password);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, userId);

        HttpResponseMessage firstReauth = await ReauthAsync(client, cookie, "wrong-password");
        HttpResponseMessage secondReauth = await ReauthAsync(client, cookie, "wrong-password");
        HttpResponseMessage firstLogin = await LoginAsync(client, email, "wrong-password");
        HttpResponseMessage secondLogin = await LoginAsync(client, email, "wrong-password");
        HttpResponseMessage lockingFailure = await ReauthAsync(
            client,
            cookie,
            "wrong-password");

        await AssertReauthFailureAsync(firstReauth);
        await AssertReauthFailureAsync(secondReauth);
        await AssertLoginFailureAsync(firstLogin);
        await AssertLoginFailureAsync(secondLogin);
        await AssertReauthFailureAsync(lockingFailure);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            AuthUser account = (await users.FindByIdAsync(userId.Value.ToString("D")))!;
            Assert.True(await users.IsLockedOutAsync(account));
        }

        HttpResponseMessage correctReauth = await ReauthAsync(client, cookie, Password);
        HttpResponseMessage correctLogin = await LoginAsync(client, email, Password);

        await AssertReauthFailureAsync(correctReauth);
        await AssertLoginFailureAsync(correctLogin);
        Assert.True(Slice5HttpTest.HeaderNames(firstReauth)
            .SetEquals(Slice5HttpTest.HeaderNames(correctReauth)));
        Assert.True(Slice5HttpTest.HeaderNames(firstLogin)
            .SetEquals(Slice5HttpTest.HeaderNames(correctLogin)));

        _factory.Clock.Advance(Duration.FromMinutes(16));
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            AuthUser account = (await users.FindByIdAsync(userId.Value.ToString("D")))!;
            Assert.True((await users.SetLockoutEndDateAsync(
                account,
                DateTimeOffset.UtcNow.AddSeconds(-1))).Succeeded);
        }

        HttpResponseMessage afterExpiry = await LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, afterExpiry.StatusCode);
        Assert.True(afterExpiry.Headers.TryGetValues(
            HeaderNames.SetCookie,
            out IEnumerable<string>? cookies));
        Assert.Contains(cookies, value => value.StartsWith("pu.auth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReauthToken_ReplayedByAnotherUser_IsRejected_D067()
    {
        UserId owner = await _factory.SeedUserAsync(
            email: "proof-owner@example.test",
            password: Password);
        UserId attacker = await _factory.SeedUserAsync(
            email: "proof-replayer@example.test",
            password: Password);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string attackerCookie = await SignInAsync(client, attacker);

        IssuedToken passwordProof = await IssueProofAsync(owner);
        HttpResponseMessage passwordSet = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/password/set",
            JsonContent.Create(new
            {
                newPassword = "attacker-chosen-password",
                reauthToken = passwordProof.RawToken,
            }),
            attackerCookie);
        await AssertReauthFailureAsync(passwordSet);

        IssuedToken emailProof = await IssueProofAsync(owner);
        HttpResponseMessage emailChange = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/change",
            JsonContent.Create(new
            {
                email = "stolen-address@example.test",
                reauthToken = emailProof.RawToken,
            }),
            attackerCookie);
        await AssertReauthFailureAsync(emailChange);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            AuthUser account = (await users.FindByIdAsync(attacker.Value.ToString("D")))!;
            Assert.True(await users.CheckPasswordAsync(account, Password));
            Assert.False(await users.CheckPasswordAsync(account, "attacker-chosen-password"));
            Assert.Equal("proof-replayer@example.test", account.Email);
        }

        Assert.Empty(_factory.EmailSender.Messages);
        Assert.Equal(
            TokenRedemptionFailure.NotFound,
            (await RedeemProofAsync(passwordProof.RawToken)).Failure);
        Assert.Equal(
            TokenRedemptionFailure.NotFound,
            (await RedeemProofAsync(emailProof.RawToken)).Failure);
    }

    [Fact]
    public async Task ExternalReauth_WithAnUnlinkedProviderIdentity_MintsNoToken_D067()
    {
        UserId victim = await _factory.SeedUserAsync(
            email: "external-reauth-victim@example.test",
            password: Password);
        await using WebApplicationFactory<Program> googleFactory =
            CreateGoogleFactory("unlinked-google-key");
        using HttpClient client = Slice5HttpTest.CreateClient(googleFactory);
        string victimCookie = await SignInAsync(client, victim);

        HttpResponseMessage completion = await CompleteExternalReauthAsync(
            client,
            victimCookie,
            victimCookie);

        Assert.Contains(
            "error=reauth_identity_mismatch",
            completion.Headers.Location?.OriginalString,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "#reauth=",
            completion.Headers.Location?.OriginalString,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalReauth_CompletedUnderAnotherUsersCookie_MintsNoToken_D067()
    {
        UserId owner = await _factory.SeedUserAsync(
            email: "external-reauth-owner@example.test",
            password: Password);
        UserId other = await _factory.SeedUserAsync(
            email: "external-reauth-other@example.test",
            password: Password);
        const string providerKey = "owners-google-key";
        await AddGoogleLoginAsync(owner, providerKey);
        await using WebApplicationFactory<Program> googleFactory =
            CreateGoogleFactory(providerKey);
        using HttpClient client = Slice5HttpTest.CreateClient(googleFactory);
        string ownerCookie = await SignInAsync(client, owner);
        string otherCookie = await SignInAsync(client, other);

        HttpResponseMessage completion = await CompleteExternalReauthAsync(
            client,
            ownerCookie,
            otherCookie);

        Assert.Contains(
            "error=reauth_identity_mismatch",
            completion.Headers.Location?.OriginalString,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "#reauth=",
            completion.Headers.Location?.OriginalString,
            StringComparison.Ordinal);
    }

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private WebApplicationFactory<Program> CreateGoogleFactory(string providerKey) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.PostConfigure<GoogleOptions>(
                    GoogleDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.Events.OnRedirectToAuthorizationEndpoint = async context =>
                        {
                            var identity = new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, providerKey),
                                new Claim(ClaimTypes.Email, "provider@example.test"),
                                new Claim("email_verified", "true"),
                            ], GoogleDefaults.AuthenticationScheme);
                            await context.HttpContext.SignInAsync(
                                IdentityConstants.ExternalScheme,
                                new ClaimsPrincipal(identity),
                                context.Properties);
                            context.Response.Redirect(context.Properties.RedirectUri!);
                        };
                    })));

    private static async Task<HttpResponseMessage> CompleteExternalReauthAsync(
        HttpClient client,
        string mintingCookie,
        string completionCookie)
    {
        HttpResponseMessage minted = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/external/intent",
            JsonContent.Create(new
            {
                provider = "google",
                mode = "reauth",
                returnTo = "/settings/account",
            }),
            mintingCookie);
        minted.EnsureSuccessStatusCode();
        ExternalIntentResponse intent =
            (await minted.Content.ReadFromJsonAsync<ExternalIntentResponse>())!;
        string binderCookie = Cookie(minted, value => value.Name.Value == "pu.intent");

        using var start = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/auth/external/google/start?intent={Uri.EscapeDataString(intent.Intent)}");
        start.Headers.Add(HeaderNames.Cookie, $"{mintingCookie}; {binderCookie}");
        HttpResponseMessage challenge = await client.SendAsync(start);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        string externalCookie = Cookie(
            challenge,
            value => value.Name.Value?.Contains(
                "Identity.External",
                StringComparison.Ordinal) == true);

        using var complete = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/auth/external/complete");
        complete.Headers.Add(
            HeaderNames.Cookie,
            $"{completionCookie}; {externalCookie}");
        return await client.SendAsync(complete);
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

    private static string Cookie(
        HttpResponseMessage response,
        Func<SetCookieHeaderValue, bool> predicate)
    {
        SetCookieHeaderValue cookie = response.Headers
            .GetValues(HeaderNames.SetCookie)
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(predicate);
        return $"{cookie.Name.Value}={cookie.Value.Value}";
    }

    private static Task<HttpResponseMessage> ReauthAsync(
        HttpClient client,
        string cookie,
        string password) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/reauth",
            JsonContent.Create(new { currentPassword = password }),
            cookie);

    private static Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string email,
        string password) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email, password }));

    private static async Task AssertReauthFailureAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AuthProblem body = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("reauth_required", body.Code);
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
    }

    private static async Task AssertLoginFailureAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AuthProblem body = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("invalid_credentials", body.Code);
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
    }

    private async Task<IssuedToken> IssueProofAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ISecurityTokenService>();
        return await tokens.IssueAsync(
            new IssueTokenRequest(
                AuthTokenPurpose.Reauthentication,
                userId,
                null,
                Duration.FromMinutes(5)),
            default);
    }

    private async Task<TokenRedemption> RedeemProofAsync(string rawToken)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ISecurityTokenService>();
        return await tokens.RedeemAsync(
            new RedeemTokenRequest(AuthTokenPurpose.Reauthentication, rawToken),
            default);
    }
}
