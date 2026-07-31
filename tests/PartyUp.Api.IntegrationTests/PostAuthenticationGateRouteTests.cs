using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class PostAuthenticationGateRouteTests : IAsyncLifetime, IDisposable
{
    private const string Password = "correct-password";
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task EveryCredentialPath_UsesThePostAuthenticationGate_D158()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        await AssertActivePasswordLoginSucceedsAsync(client);
        await AssertBannedPasswordLoginIsRefusedAsync(client);
        await AssertBannedTokenCredentialIsRefusedAsync(
            client,
            AuthTokenPurpose.MagicLink,
            "/api/v1/auth/magic-link/consume",
            token => new { token, ageGateAttested = true });
        await AssertBannedTokenCredentialIsRefusedAsync(
            client,
            AuthTokenPurpose.PasswordReset,
            "/api/v1/auth/password/reset",
            token => new { token, newPassword = "replacement-password" });

        await AssertPendingPasswordLoginRestoresAsync(client);
        await AssertPendingTokenCredentialRestoresAsync(
            client,
            AuthTokenPurpose.MagicLink,
            "/api/v1/auth/magic-link/consume",
            token => new { token, ageGateAttested = true });
        await AssertPendingTokenCredentialRestoresAsync(
            client,
            AuthTokenPurpose.PasswordReset,
            "/api/v1/auth/password/reset",
            token => new { token, newPassword = "replacement-password" });

        await AssertPreAccountVerificationUsesTheSessionDoorAsync(client);
    }

    private async Task AssertActivePasswordLoginSucceedsAsync(HttpClient client)
    {
        const string email = "active-login@example.test";
        UserId userId = await _factory.SeedUserAsync(email: email, password: Password);

        _factory.PostAuthenticationGateProbe.Reset();
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email, password = Password }));

        Assert.Equal(1, _factory.PostAuthenticationGateProbe.Entries);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertAuthCookie(response);
        await AssertActiveAsync(userId);
    }

    private async Task AssertBannedPasswordLoginIsRefusedAsync(HttpClient client)
    {
        const string email = "banned-login@example.test";
        await _factory.SeedUserAsync(
            banned: true,
            email: email,
            password: Password);

        _factory.PostAuthenticationGateProbe.Reset();
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email, password = Password }));

        Assert.Equal(1, _factory.PostAuthenticationGateProbe.Entries);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoAuthCookie(response);
    }

    private async Task AssertBannedTokenCredentialIsRefusedAsync(
        HttpClient client,
        AuthTokenPurpose purpose,
        string route,
        Func<string, object> body)
    {
        string email = $"banned-{purpose.ToString().ToLowerInvariant()}@example.test";
        UserId userId = await _factory.SeedUserAsync(
            banned: true,
            email: email,
            password: Password);
        IssuedToken issued = await IssueAsync(purpose, userId, email);

        _factory.PostAuthenticationGateProbe.Reset();
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            route,
            JsonContent.Create(body(issued.RawToken)));

        Assert.Equal(1, _factory.PostAuthenticationGateProbe.Entries);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoAuthCookie(response);
    }

    private async Task AssertPendingPasswordLoginRestoresAsync(HttpClient client)
    {
        const string email = "pending-login@example.test";
        UserId userId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: email,
            password: Password);

        _factory.PostAuthenticationGateProbe.Reset();
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email, password = Password }));

        Assert.Equal(1, _factory.PostAuthenticationGateProbe.Entries);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertAuthCookie(response);
        await AssertActiveAsync(userId);
    }

    private async Task AssertPendingTokenCredentialRestoresAsync(
        HttpClient client,
        AuthTokenPurpose purpose,
        string route,
        Func<string, object> body)
    {
        string email = $"pending-{purpose.ToString().ToLowerInvariant()}@example.test";
        UserId userId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: email,
            password: Password);
        IssuedToken issued = await IssueAsync(purpose, userId, email);

        _factory.PostAuthenticationGateProbe.Reset();
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            route,
            JsonContent.Create(body(issued.RawToken)));

        Assert.Equal(1, _factory.PostAuthenticationGateProbe.Entries);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertAuthCookie(response);
        await AssertActiveAsync(userId);
    }

    private async Task AssertPreAccountVerificationUsesTheSessionDoorAsync(HttpClient client)
    {
        const string email = "verification-gate@example.test";
        IssuedToken issued;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<ISecurityTokenService>();
            issued = await tokens.IssueAsync(
                new IssueTokenRequest(
                    AuthTokenPurpose.EmailVerification,
                    null,
                    email.ToUpperInvariant(),
                    Duration.FromHours(24),
                    new PendingRegistration("en", true),
                    Email: email),
                default);
        }

        _factory.PostAuthenticationGateProbe.Reset();
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/verify",
            JsonContent.Create(new
            {
                token = issued.RawToken,
                password = Password,
                ageGateAttested = true,
            }));

        Assert.Equal(1, _factory.PostAuthenticationGateProbe.Entries);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertAuthCookie(response);
    }

    private async Task<IssuedToken> IssueAsync(
        AuthTokenPurpose purpose,
        UserId userId,
        string email)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ISecurityTokenService>();
        return await tokens.IssueAsync(
            new IssueTokenRequest(
                purpose,
                userId,
                email.ToUpperInvariant(),
                Duration.FromHours(1),
                Email: email),
            default);
    }

    private async Task AssertActiveAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserLifecycle lifecycle = await context.DomainUsers
            .Where(user => user.Id == userId)
            .Select(user => user.Lifecycle)
            .SingleAsync();
        Assert.Equal(UserLifecycle.Active, lifecycle);
    }

    private static void AssertAuthCookie(HttpResponseMessage response)
    {
        Assert.Contains(
            response.Headers.GetValues(HeaderNames.SetCookie),
            value => value.StartsWith("pu.auth=", StringComparison.Ordinal));
    }

    private static void AssertNoAuthCookie(HttpResponseMessage response)
    {
        Assert.False(
            response.Headers.TryGetValues(HeaderNames.SetCookie, out IEnumerable<string>? values) &&
            values.Any(value => value.StartsWith("pu.auth=", StringComparison.Ordinal)));
    }
}
