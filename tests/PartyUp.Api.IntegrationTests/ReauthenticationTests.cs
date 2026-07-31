using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
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
    private readonly PartyUpApiFactory _factory = new();

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
