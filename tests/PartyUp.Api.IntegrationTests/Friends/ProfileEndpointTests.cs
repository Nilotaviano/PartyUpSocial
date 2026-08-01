using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class ProfileEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ProtectedRoutes_ReturnExactAuthenticationAndOnboardingProblems()
    {
        using HttpClient anonymous = Slice5HttpTest.CreateClient(_factory);
        HttpResponseMessage unauthorized = await anonymous.GetAsync("/api/v1/friends");
        await FriendEndpointTest.AssertProblemAsync(
            unauthorized,
            HttpStatusCode.Unauthorized,
            "unauthorized",
            "/api/v1/friends");

        FriendEndpointUser incomplete = await FriendEndpointTest.SeedAsync(
            _factory,
            completeProfile: false);
        using FriendEndpointSession session = await FriendEndpointTest.SignInAsync(
            _factory,
            incomplete.Id);
        HttpResponseMessage forbidden = await session.GetAsync("/api/v1/friend-share");
        await FriendEndpointTest.AssertProblemAsync(
            forbidden,
            HttpStatusCode.Forbidden,
            "onboarding_required",
            "/api/v1/friend-share");
    }

    [Fact]
    public async Task ProfileAndShare_ExposeOnlyTheirDocumentedShapes()
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser target = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession session = await FriendEndpointTest.SignInAsync(
            _factory,
            actor.Id);

        using JsonDocument profile = await FriendEndpointTest.SuccessJsonAsync(
            await session.GetAsync($"/api/v1/users/by-username/{target.Username}"));
        JsonElement root = profile.RootElement;
        Assert.Single(root.EnumerateObject());
        JsonElement view = root.GetProperty("profile");
        Assert.Equal("username_only", view.GetProperty("disclosure").GetString());
        Assert.Equal(target.Username, view.GetProperty("username").GetString());
        Assert.Equal("none", view.GetProperty("relationship").GetProperty("kind").GetString());
        Assert.False(view.TryGetProperty("displayName", out _));
        Assert.False(view.TryGetProperty("friendCode", out _));

        using JsonDocument share = await FriendEndpointTest.SuccessJsonAsync(
            await session.GetAsync("/api/v1/friend-share"));
        Assert.Equal(actor.FriendCode, share.RootElement.GetProperty("friendCode").GetString());
        Assert.True(Uri.TryCreate(
            share.RootElement.GetProperty("addUrl").GetString(),
            UriKind.Absolute,
            out _));
        Assert.True(Uri.TryCreate(
            share.RootElement.GetProperty("profileUrl").GetString(),
            UriKind.Absolute,
            out _));
    }
}

internal sealed record FriendEndpointUser(UserId Id, string Username, string FriendCode);

internal sealed class FriendEndpointSession(HttpClient client, string authCookie) : IDisposable
{
    internal HttpClient Client => client;
    internal string AuthCookie => authCookie;

    internal Task<HttpResponseMessage> GetAsync(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(HeaderNames.Cookie, authCookie);
        return client.SendAsync(request);
    }

    internal Task<HttpResponseMessage> SendUnsafeAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null) =>
        AntiforgeryTestClient.SendAsync(
            client,
            method,
            path,
            content,
            authCookie);

    public void Dispose() => client.Dispose();
}

internal static class FriendEndpointTest
{
    internal static async Task<FriendEndpointUser> SeedAsync(
        PartyUpApiFactory factory,
        bool completeProfile = true)
    {
        string suffix = Guid.NewGuid().ToString("N");
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        UserId id = UserId.New();
        string friendCode = string.Concat(
            Guid.NewGuid().ToByteArray()
                .Take(FriendCodeFormat.Length)
                .Select(value => FriendCodeFormat.Alphabet[
                    value % FriendCodeFormat.Alphabet.Length]));
        User user = User.BeginOnboarding(
            id,
            FriendCode.Create(friendCode),
            AgeGateSource.SelfAttested,
            factory.Clock);
        string username = string.Empty;
        if (completeProfile)
        {
            username = $"user{suffix[^12..]}";
            user.CompleteOnboarding(
                Username.Create(username),
                DisplayName.Create("HTTP Friend"),
                Avatar.FromEmoji("🙂"),
                DateTimeZoneProviders.Tzdb["America/Fortaleza"],
                Locale.Create("en"),
                factory.Clock);
            context.UsernameRegistry.Add(
                UsernameRegistryEntry.ClaimNew(user.Username!.Value, id));
        }

        context.DomainUsers.Add(user);
        await context.SaveChangesAsync();
        string identityName = id.Value.ToString("D");
        string email = $"friend-http-{suffix}@example.test";
        IdentityResult identity = await userManager.CreateAsync(new AuthUser
        {
            Id = id.Value,
            UserName = identityName,
            NormalizedUserName = identityName,
            Email = email,
            EmailConfirmed = true,
            LockoutEnabled = true,
        });
        Assert.True(identity.Succeeded);
        return new FriendEndpointUser(
            id,
            username,
            friendCode);
    }

    internal static async Task<FriendEndpointSession> SignInAsync(
        PartyUpApiFactory factory,
        UserId userId)
    {
        HttpClient client = Slice5HttpTest.CreateClient(factory);
        HttpResponseMessage signIn = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        return new FriendEndpointSession(client, Slice5HttpTest.AuthCookie(signIn));
    }

    internal static async Task<JsonDocument> SuccessJsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    internal static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        string instance)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(6, root.EnumerateObject().Count());
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        Assert.Equal((int)status, root.GetProperty("status").GetInt32());
        Assert.Equal(instance, root.GetProperty("instance").GetString());
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.False(root.TryGetProperty("detail", out _));
    }
}
