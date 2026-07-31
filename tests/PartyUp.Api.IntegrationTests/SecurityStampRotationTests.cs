using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SecurityStampRotationTests : IAsyncLifetime, IDisposable
{
    private const string Password = "current-password";
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task CredentialMutation_InvalidatesEveryPreexistingSession_D165()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        await AssertPasswordResetInvalidatesAsync(client);
        await AssertPasswordChangeInvalidatesAsync(client);
        await AssertPasswordSetInvalidatesAsync(client);
        await AssertEmailChangeInvalidatesAsync(client);
    }

    private async Task AssertPasswordResetInvalidatesAsync(HttpClient client)
    {
        UserId userId = await _factory.SeedUserAsync(
            email: "stamp-reset@example.test",
            password: Password);
        string[] oldCookies = await CreateTwoCookiesAsync(client, userId);
        IssuedToken token = await IssueAsync(
            AuthTokenPurpose.PasswordReset,
            userId,
            "stamp-reset@example.test");

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/password/reset",
            JsonContent.Create(new
            {
                token = token.RawToken,
                newPassword = "replacement-password",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertOldCookiesRejectedAsync(
            client,
            oldCookies,
            Slice5HttpTest.AuthCookie(response));
    }

    private async Task AssertPasswordChangeInvalidatesAsync(HttpClient client)
    {
        UserId userId = await _factory.SeedUserAsync(
            email: "stamp-change@example.test",
            password: Password);
        string[] oldCookies = await CreateTwoCookiesAsync(client, userId);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/password/change",
            JsonContent.Create(new
            {
                currentPassword = Password,
                newPassword = "replacement-password",
            }),
            oldCookies[0]);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertOldCookiesRejectedAsync(
            client,
            oldCookies,
            Slice5HttpTest.AuthCookie(response));
    }

    private async Task AssertPasswordSetInvalidatesAsync(HttpClient client)
    {
        UserId userId = await _factory.SeedUserAsync(
            email: "stamp-set@example.test");
        string[] oldCookies = await CreateTwoCookiesAsync(client, userId);
        IssuedToken proof = await IssueAsync(
            AuthTokenPurpose.Reauthentication,
            userId,
            email: null);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/password/set",
            JsonContent.Create(new
            {
                newPassword = "newly-set-password",
                reauthToken = proof.RawToken,
            }),
            oldCookies[0]);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertOldCookiesRejectedAsync(
            client,
            oldCookies,
            Slice5HttpTest.AuthCookie(response));
    }

    private async Task AssertEmailChangeInvalidatesAsync(HttpClient client)
    {
        UserId userId = await _factory.SeedUserAsync(
            email: "stamp-email-old@example.test",
            password: Password);
        string[] oldCookies = await CreateTwoCookiesAsync(client, userId);
        IssuedToken proof = await IssueAsync(
            AuthTokenPurpose.Reauthentication,
            userId,
            email: null);

        HttpResponseMessage requested = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/change",
            JsonContent.Create(new
            {
                email = "stamp-email-new@example.test",
                reauthToken = proof.RawToken,
            }),
            oldCookies[0]);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        EmailMessage verification = await _factory.EmailSender.WaitForAsync(
            message => message.ToAddress == "stamp-email-new@example.test" &&
                message.Template == EmailTemplate.EmailChangeVerification);

        HttpResponseMessage verified = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/change/verify",
            JsonContent.Create(new { token = verification.Model["Token"] }));

        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
        Assert.False(
            verified.Headers.TryGetValues(HeaderNames.SetCookie, out IEnumerable<string>? values) &&
            values.Any(value => value.StartsWith("pu.auth=", StringComparison.Ordinal)));
        await AssertOldCookiesRejectedAsync(client, oldCookies, freshCookie: null);
    }

    private static async Task<string[]> CreateTwoCookiesAsync(HttpClient client, UserId userId) =>
    [
        await SignInAsync(client, userId),
        await SignInAsync(client, userId),
    ];

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Slice5HttpTest.AuthCookie(response);
    }

    private static async Task AssertOldCookiesRejectedAsync(
        HttpClient client,
        IEnumerable<string> oldCookies,
        string? freshCookie)
    {
        foreach (string cookie in oldCookies)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/v1/_slice2/authenticated");
            request.Headers.Add(HeaderNames.Cookie, cookie);
            HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        if (freshCookie is null)
        {
            return;
        }

        using var freshRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/_slice2/authenticated");
        freshRequest.Headers.Add(HeaderNames.Cookie, freshCookie);
        HttpResponseMessage freshResponse = await client.SendAsync(freshRequest);
        Assert.Equal(HttpStatusCode.NoContent, freshResponse.StatusCode);
    }

    private async Task<IssuedToken> IssueAsync(
        AuthTokenPurpose purpose,
        UserId userId,
        string? email)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ISecurityTokenService>();
        return await tokens.IssueAsync(
            new IssueTokenRequest(
                purpose,
                userId,
                email?.ToUpperInvariant(),
                Duration.FromHours(1),
                Email: email),
            default);
    }
}
