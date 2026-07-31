using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class DeletionPurgeTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Sweep_LeavesNoAuthTokenRowForTheDeletedUser_D092()
    {
        const string formerAddress = "purged-token@example.test";
        UserId userId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: formerAddress);
        IssuedToken token = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.PasswordReset,
            userId,
            formerAddress.ToUpperInvariant(),
            Duration.FromDays(30),
            Email: formerAddress));
        _factory.Clock.Advance(User.DeletionGracePeriod);

        await RunSweepAsync();

        TokenRedemption redemption = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.PasswordReset,
            token.RawToken));
        Assert.False(redemption.Succeeded);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AccountEmailInfo? email = await scope.ServiceProvider
            .GetRequiredService<IUserAccountGateway>()
            .GetEmailAsync(userId, default);
        Assert.Null(email);
    }

    [Fact]
    public async Task Sweep_PurgesTheIdentityCredentials_D092()
    {
        const string email = "purged-credentials@example.test";
        const string googleKey = "google-purge-key";
        const string steamKey = "76561198000000001";
        UserId userId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: email,
            password: "Correct-Purge-Password-42!");
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            IUserAccountGateway accounts = scope.ServiceProvider
                .GetRequiredService<IUserAccountGateway>();
            await accounts.AddLoginAsync(
                userId,
                ExternalProviderPolicy.Google,
                googleKey,
                default);
            await accounts.AddLoginAsync(
                userId,
                ExternalProviderPolicy.Steam,
                steamKey,
                default);
            await accounts.UpsertSteamIdentityAsync(
                userId,
                steamKey,
                "Former Player",
                null,
                _factory.Clock.GetCurrentInstant(),
                default);
        }
        _factory.Clock.Advance(User.DeletionGracePeriod);

        await RunSweepAsync();

        await using AsyncServiceScope verificationScope =
            _factory.Services.CreateAsyncScope();
        IUserAccountGateway gateway = verificationScope.ServiceProvider
            .GetRequiredService<IUserAccountGateway>();
        Assert.Null(await gateway.GetEmailAsync(userId, default));
        Assert.False(await gateway.HasPasswordAsync(userId, default));
        Assert.Empty(await gateway.ListLoginProvidersAsync(userId, default));
        Assert.Null(await gateway.FindUserIdByLoginAsync(
            ExternalProviderPolicy.Google,
            googleKey,
            default));
        Assert.Null(await gateway.FindUserIdByLoginAsync(
            ExternalProviderPolicy.Steam,
            steamKey,
            default));
    }

    [Fact]
    public async Task Sweep_WithAFailingCredentialPurge_LeavesNoTombstone_D092()
    {
        UserId userId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: "purge-failing-user@example.test");
        UserId admin = await _factory.SeedUserAsync(
            admin: true,
            email: "purge-failing-admin@example.test");
        _factory.Clock.Advance(User.DeletionGracePeriod);
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserAccountGateway>();
                services.AddScoped<UserAccountGateway>();
                services.AddScoped<IUserAccountGateway>(provider =>
                    new FailingPurgeAccountGateway(
                        provider.GetRequiredService<UserAccountGateway>()));
            }));
        using HttpClient client = Slice5HttpTest.CreateClient(factory);
        string cookie = await SignInAsync(client, admin);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/admin/maintenance/deletion-sweep",
            content: null,
            cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        User user = (await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .FindAsync(userId, default))!;
        Assert.True(user.IsPendingDeletion);
        Assert.False(user.IsTombstoned);
    }

    private async Task RunSweepAsync()
    {
        UserId admin = await _factory.SeedUserAsync(admin: true);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, admin);
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/admin/maintenance/deletion-sweep",
            content: null,
            cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<IssuedToken> IssueAsync(IssueTokenRequest request)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISecurityTokenService>()
            .IssueAsync(request, default);
    }

    private async Task<TokenRedemption> RedeemAsync(RedeemTokenRequest request)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISecurityTokenService>()
            .RedeemAsync(request, default);
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
}

internal sealed class FailingPurgeAccountGateway(
    IUserAccountGateway inner) : IUserAccountGateway
{
    public Task<UserId?> FindUserIdByEmailAsync(
        string email,
        CancellationToken cancellationToken) =>
        inner.FindUserIdByEmailAsync(email, cancellationToken);

    public Task<UserId?> FindUserIdByLoginAsync(
        string provider,
        string providerKey,
        CancellationToken cancellationToken) =>
        inner.FindUserIdByLoginAsync(provider, providerKey, cancellationToken);

    public Task CreateAccountAsync(
        UserId id,
        string? email,
        bool emailConfirmed,
        string? password,
        CancellationToken cancellationToken) =>
        inner.CreateAccountAsync(id, email, emailConfirmed, password, cancellationToken);

    public Task<PasswordCheckResult> CheckPasswordAsync(
        UserId id,
        string password,
        CancellationToken cancellationToken) =>
        inner.CheckPasswordAsync(id, password, cancellationToken);

    public Task VerifyDummyPasswordAsync(CancellationToken cancellationToken) =>
        inner.VerifyDummyPasswordAsync(cancellationToken);

    public Task SetPasswordAsync(
        UserId id,
        string password,
        CancellationToken cancellationToken) =>
        inner.SetPasswordAsync(id, password, cancellationToken);

    public Task RemovePasswordAsync(UserId id, CancellationToken cancellationToken) =>
        inner.RemovePasswordAsync(id, cancellationToken);

    public Task SetEmailAsync(
        UserId id,
        string email,
        bool confirmed,
        CancellationToken cancellationToken) =>
        inner.SetEmailAsync(id, email, confirmed, cancellationToken);

    public Task ConfirmEmailAsync(UserId id, CancellationToken cancellationToken) =>
        inner.ConfirmEmailAsync(id, cancellationToken);

    public Task<AccountEmailInfo?> GetEmailAsync(
        UserId id,
        CancellationToken cancellationToken) =>
        inner.GetEmailAsync(id, cancellationToken);

    public Task AddLoginAsync(
        UserId id,
        string provider,
        string providerKey,
        CancellationToken cancellationToken) =>
        inner.AddLoginAsync(id, provider, providerKey, cancellationToken);

    public Task RemoveLoginAsync(
        UserId id,
        string provider,
        CancellationToken cancellationToken) =>
        inner.RemoveLoginAsync(id, provider, cancellationToken);

    public Task<IReadOnlyList<string>> ListLoginProvidersAsync(
        UserId id,
        CancellationToken cancellationToken) =>
        inner.ListLoginProvidersAsync(id, cancellationToken);

    public Task<bool> HasPasswordAsync(UserId id, CancellationToken cancellationToken) =>
        inner.HasPasswordAsync(id, cancellationToken);

    public Task UpsertSteamIdentityAsync(
        UserId id,
        string steamId64,
        string personaName,
        string? avatarUrl,
        Instant fetchedAt,
        CancellationToken cancellationToken) =>
        inner.UpsertSteamIdentityAsync(
            id,
            steamId64,
            personaName,
            avatarUrl,
            fetchedAt,
            cancellationToken);

    public Task RemoveSteamIdentityAsync(
        UserId id,
        CancellationToken cancellationToken) =>
        inner.RemoveSteamIdentityAsync(id, cancellationToken);

    public Task PurgeCredentialsAsync(
        UserId id,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("injected credential purge failure");

    public Task EnsureRoleExistsAsync(
        string role,
        CancellationToken cancellationToken) =>
        inner.EnsureRoleExistsAsync(role, cancellationToken);

    public Task<bool> AddToRoleAsync(
        UserId id,
        string role,
        CancellationToken cancellationToken) =>
        inner.AddToRoleAsync(id, role, cancellationToken);
}
