using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class PlatformLinkRouteTests : IAsyncLifetime, IDisposable
{
    private const string Password = "correct-password";
    private const string SteamId = "76561198000000000";
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Delete_ForASteamLink_Returns409WithoutProofAndRemovesAllThreeRowsWithIt_D137()
    {
        UserId userId = await _factory.SeedUserAsync(password: Password);
        (PlatformLinkId linkId, PlatformLinkId nonSteamLinkId) = await SeedLinksAsync(userId);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string authCookie = await SignInAsync(client, userId);

        HttpResponseMessage nonSteamRemoved = await DeleteAsync(
            client,
            nonSteamLinkId,
            authCookie,
            content: null);
        HttpResponseMessage refused = await DeleteAsync(
            client,
            linkId,
            authCookie,
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));
        HttpResponseMessage removed = await DeleteAsync(
            client,
            linkId,
            authCookie,
            JsonContent.Create(new { currentPassword = Password }));

        Assert.Equal(HttpStatusCode.NoContent, nonSteamRemoved.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        AuthProblem problem = (await refused.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("reauth_required", problem.Code);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await context.PlatformLinks.AnyAsync(link => link.UserId == userId));
        Assert.False(await context.SteamIdentities.AnyAsync(identity => identity.UserId == userId));
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        AuthUser user = (await manager.FindByIdAsync(userId.Value.ToString("D")))!;
        Assert.DoesNotContain(
            await manager.GetLoginsAsync(user),
            login => login.LoginProvider == "steam");

        // A-049: unlinking a sign-in method rotates the security stamp, and the
        // stamp validator rejects every pre-existing session on its next
        // request — including the caller's own cookie.
        HttpResponseMessage rotatedOut = await DeleteAsync(
            client,
            linkId,
            authCookie,
            content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, rotatedOut.StatusCode);
    }

    private async Task<(PlatformLinkId Steam, PlatformLinkId NonSteam)> SeedLinksAsync(
        UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User user = (await context.DomainUsers.SingleAsync(value => value.Id == userId));
        PlatformLink link = user.LinkPlatform(Platform.Steam, GamingHandle.Create("Steam Player"));
        PlatformLink nonSteam = user.LinkPlatform(
            Platform.Xbox,
            GamingHandle.Create("Xbox Player"));
        context.SteamIdentities.Add(new SteamIdentity(
            userId,
            SteamId,
            "Steam Player",
            "https://cdn.example/avatar.jpg",
            _factory.Clock.GetCurrentInstant()));
        await context.SaveChangesAsync();

        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        AuthUser authUser = (await manager.FindByIdAsync(userId.Value.ToString("D")))!;
        Assert.True((await manager.AddLoginAsync(
            authUser,
            new UserLoginInfo("steam", SteamId, "Steam"))).Succeeded);
        return (link.Id, nonSteam.Id);
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

    private static async Task<HttpResponseMessage> DeleteAsync(
        HttpClient client,
        PlatformLinkId linkId,
        string authCookie,
        HttpContent? content)
    {
        AntiforgeryPair pair = await AntiforgeryTestClient.GetAsync(client, authCookie);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v1/me/platform-links/{linkId.Value:D}")
        {
            Content = content,
        };
        request.Headers.Add("X-CSRF-TOKEN", pair.Token);
        request.Headers.Add(HeaderNames.Cookie, $"{authCookie}; {pair.Cookie}");
        return await client.SendAsync(request);
    }
}
