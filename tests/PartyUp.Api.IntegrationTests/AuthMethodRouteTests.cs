using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AuthMethodRouteTests : IAsyncLifetime, IDisposable
{
    private const string Password = "correct-password";
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Get_ListsMagicLinkWithItsRemovableFlag_D067()
    {
        UserId userId = await _factory.SeedUserAsync(password: Password);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string authCookie = await SignInAsync(client, userId);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/methods");
        request.Headers.Add(HeaderNames.Cookie, authCookie);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AuthMethodsResponse body =
            (await response.Content.ReadFromJsonAsync<AuthMethodsResponse>())!;
        Assert.Equal(
            ["password", "magicLink", "google", "steam"],
            body.Methods.Select(method => method.Kind));
        AuthMethodResponse magicLink = Assert.Single(
            body.Methods,
            method => method.Kind == "magicLink");
        Assert.True(magicLink.Linked);
        Assert.False(magicLink.Removable);
    }

    [Fact]
    public async Task Delete_RequiresProofBeforeTheMethodSpecificRefusal()
    {
        UserId userId = await _factory.SeedUserAsync(password: Password);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string authCookie = await SignInAsync(client, userId);

        HttpResponseMessage response = await DeleteAsync(
            client,
            "magicLink",
            authCookie,
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("reauth_required", problem.Code);
    }

    [Fact]
    public async Task Delete_WithProof_RemovesAllProviderRowsAndInvalidatesCookie_D087()
    {
        UserId userId = await _factory.SeedUserAsync(password: Password);
        await AddGoogleLoginsAsync(userId);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string authCookie = await SignInAsync(client, userId);

        HttpResponseMessage response = await DeleteAsync(
            client,
            "google",
            authCookie,
            JsonContent.Create(new { currentPassword = Password }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await context.Set<IdentityUserLogin<Guid>>().AnyAsync(login =>
                login.UserId == userId.Value && login.LoginProvider == "google"));
        }

        using var oldSession = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/_slice2/authenticated");
        oldSession.Headers.Add(HeaderNames.Cookie, authCookie);
        HttpResponseMessage rotatedOut = await client.SendAsync(oldSession);
        Assert.Equal(HttpStatusCode.Unauthorized, rotatedOut.StatusCode);
    }

    [Fact]
    public async Task Delete_MagicLinkWithProof_ReturnsMethodNotRemovable()
    {
        UserId userId = await _factory.SeedUserAsync(password: Password);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string authCookie = await SignInAsync(client, userId);

        HttpResponseMessage response = await DeleteAsync(
            client,
            "magicLink",
            authCookie,
            JsonContent.Create(new { currentPassword = Password }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("method_not_removable", problem.Code);
    }

    [Fact]
    public async Task Delete_LastRemainingMethod_ReturnsConflict_D137()
    {
        UserId userId = await _factory.SeedUserAsync(email: null, password: Password);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string authCookie = await SignInAsync(client, userId);

        HttpResponseMessage response = await DeleteAsync(
            client,
            "password",
            authCookie,
            JsonContent.Create(new { currentPassword = Password }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("last_auth_method", problem.Code);
    }

    private async Task AddGoogleLoginsAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        AuthUser user = (await manager.FindByIdAsync(userId.Value.ToString("D")))!;
        Assert.True((await manager.AddLoginAsync(
            user,
            new UserLoginInfo("google", "google-key-one", "Google"))).Succeeded);
        Assert.True((await manager.AddLoginAsync(
            user,
            new UserLoginInfo("google", "google-key-two", "Google"))).Succeeded);
    }

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Slice5HttpTest.AuthCookie(response);
    }

    private static Task<HttpResponseMessage> DeleteAsync(
        HttpClient client,
        string kind,
        string authCookie,
        HttpContent? content) =>
        AntiforgeryTestClient.SendAsync(
            client,
            HttpMethod.Delete,
            $"/api/v1/auth/methods/{kind}",
            content,
            authCookie);
}
