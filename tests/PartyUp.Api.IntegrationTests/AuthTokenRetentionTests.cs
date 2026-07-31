using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AuthTokenRetentionTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Purge_RemovesExpiredAndConsumedRowsIncludingPreAccountAddresses_D067()
    {
        await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.PasswordReset,
            null,
            "NEVER-REGISTERED@EXAMPLE.TEST",
            Duration.FromMinutes(1)));
        IssuedToken consumed = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.MagicLink,
            null,
            "CONSUMED-PRE-ACCOUNT@EXAMPLE.TEST",
            Duration.FromDays(90),
            new PendingRegistration("en", true)));
        await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.MagicLink,
            consumed.RawToken,
            AgeGateAttested: true));

        _factory.Clock.Advance(Duration.FromDays(31));
        IssuedToken live = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.PasswordReset,
            null,
            "LIVE-PRE-ACCOUNT@EXAMPLE.TEST",
            Duration.FromHours(1)));
        UserId admin = await _factory.SeedUserAsync(admin: true);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, admin);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/admin/maintenance/deletion-sweep",
            content: null,
            cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        DeletionMaintenanceResponse result =
            (await response.Content.ReadFromJsonAsync<DeletionMaintenanceResponse>())!;
        Assert.Equal(2, result.TokensPurged);
        Assert.True((await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.PasswordReset,
            live.RawToken))).Succeeded);
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
