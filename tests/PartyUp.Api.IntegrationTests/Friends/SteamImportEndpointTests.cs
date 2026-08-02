using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Notifications;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class SteamImportEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();
    private readonly MutableSteamFriendListClient _steam = new();
    private WebApplicationFactory<Program>? _configured;

    public async Task InitializeAsync()
    {
        await _factory.StartAsync();
        _configured = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamFriendListClient>();
                services.AddSingleton<ISteamFriendListClient>(_steam);
            }));
    }

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose()
    {
        _configured?.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task ProviderStates_Are200DataNotProblemResponses_D049()
    {
        (SteamFriendListResult Provider, string State)[] cases =
        [
            (new SteamFriendListPrivateProfile(), "private_profile"),
            (new SteamFriendListSuccess([]), "zero_friends"),
            (new SteamFriendListSuccess([new SteamFriend("absent", null, null)]), "zero_matches"),
            (new SteamFriendListNotConfigured(), "not_configured"),
            (new SteamFriendListUpstreamFailure(), "upstream_failure"),
        ];

        foreach ((SteamFriendListResult provider, string state) in cases)
        {
            FriendEndpointUser actor = await SeedLinkedAsync();
            _steam.Result = provider;
            using FriendEndpointSession session = await SignInAsync(actor.Id);

            HttpResponseMessage response = await session.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/friends/imports/steam");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEqual(
                "application/problem+json",
                response.Content.Headers.ContentType?.MediaType);
            using JsonDocument body = await FriendEndpointTest.SuccessJsonAsync(response);
            Assert.Equal(state, body.RootElement.GetProperty("state").GetString());
            Assert.False(body.RootElement.TryGetProperty("detail", out _));
        }

        FriendEndpointUser readyActor = await SeedLinkedAsync("ready-importer");
        FriendEndpointUser candidate = await SeedLinkedAsync("ready-candidate");
        _steam.Result = new SteamFriendListSuccess(
            [new SteamFriend("ready-candidate", "Candidate", null)]);
        using FriendEndpointSession readySession = await SignInAsync(readyActor.Id);
        HttpResponseMessage readyResponse = await readySession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friends/imports/steam");
        using JsonDocument readyBody = await FriendEndpointTest.SuccessJsonAsync(readyResponse);
        Assert.Equal("ready", readyBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(
            candidate.Username,
            readyBody.RootElement.GetProperty("matches")[0].GetProperty("username").GetString());
    }

    [Fact]
    public async Task BlockedAndAbsentLocalCandidates_AreByteEffectAndLimiterEquivalent_D065()
    {
        const string importedSteamId = "same-steam-id";
        FriendEndpointUser actor = await SeedLinkedAsync("blocked-importer");
        FriendEndpointUser candidate = await SeedLinkedAsync(importedSteamId);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Blocks.Add(Block.Create(actor.Id, candidate.Id, _factory.Clock));
            await context.SaveChangesAsync();
        }

        _steam.Result = new SteamFriendListSuccess(
            [new SteamFriend(importedSteamId, "Same Persona", "https://cdn.test/same.png")]);
        using FriendEndpointSession session = await SignInAsync(actor.Id);
        JsonElement blocked = await ImportJsonAsync(session);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SteamIdentity identity = await context.SteamIdentities.SingleAsync(value =>
                value.SteamId64 == importedSteamId);
            context.SteamIdentities.Remove(identity);
            await context.SaveChangesAsync();
        }

        JsonElement absent = await ImportJsonAsync(session);
        Assert.Equal(Normalize(blocked), Normalize(absent));

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verificationContext =
            verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            0,
            await verificationContext.Notifications.CountAsync(notification =>
                notification.Type == NotificationType.SteamImportMatch));
    }

    [Fact]
    public async Task OpenApi_DeclaresSecuredSteamResultUnionAndFailures()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_configured!);
        using JsonDocument document = JsonDocument.Parse(
            await client.GetStringAsync("/openapi/v1.json"));
        JsonElement operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/friends/imports/steam")
            .GetProperty("post");

        Assert.Equal("ImportSteamFriends", operation.GetProperty("operationId").GetString());
        Assert.Equal(
            ["200", "400", "401", "403", "409", "429", "500"],
            operation.GetProperty("responses").EnumerateObject()
                .Select(response => response.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Single(operation.GetProperty("security").EnumerateArray());
        JsonElement union = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("SteamFriendImportResponse");
        Assert.Equal(6, union.GetProperty("oneOf").GetArrayLength());
        Assert.False(union.TryGetProperty("anyOf", out _));
    }

    private static async Task<JsonElement> ImportJsonAsync(FriendEndpointSession session)
    {
        HttpResponseMessage response = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friends/imports/steam");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await FriendEndpointTest.SuccessJsonAsync(response);
        return document.RootElement.Clone();
    }

    private static string Normalize(JsonElement root)
    {
        JsonElement unmatched = Assert.Single(root.GetProperty("unmatched").EnumerateArray());
        return JsonSerializer.Serialize(new
        {
            State = root.GetProperty("state").GetString(),
            SteamId = unmatched.GetProperty("steamId64").GetString(),
            Persona = unmatched.GetProperty("personaName").GetString(),
            Avatar = unmatched.GetProperty("avatarUrl").GetString(),
        });
    }

    private async Task<FriendEndpointUser> SeedLinkedAsync(string? steamId64 = null)
    {
        FriendEndpointUser user = await FriendEndpointTest.SeedAsync(_factory);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.SteamIdentities.Add(new SteamIdentity(
            user.Id,
            steamId64 ?? $"s{user.Id.Value:N}"[..20],
            "Steam User",
            null,
            _factory.Clock.GetCurrentInstant()));
        await context.SaveChangesAsync();
        return user;
    }

    private async Task<FriendEndpointSession> SignInAsync(PartyUp.Domain.Kernel.UserId userId)
    {
        HttpClient client = Slice5HttpTest.CreateClient(_configured!);
        HttpResponseMessage signIn = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        return new FriendEndpointSession(client, Slice5HttpTest.AuthCookie(signIn));
    }
}

internal sealed class MutableSteamFriendListClient : ISteamFriendListClient
{
    internal SteamFriendListResult Result { get; set; } = new SteamFriendListNotConfigured();

    public Task<SteamFriendListResult> GetAsync(
        string steamId64,
        CancellationToken cancellationToken) => Task.FromResult(Result);
}
