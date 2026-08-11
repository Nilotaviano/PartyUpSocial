using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
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
                services.RemoveAll<ISteamProfileClient>();
                services.AddSingleton<ISteamProfileClient>(new StaticSteamProfileClient());
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
        // D-188: a matched user is a real, visible PartyUp account (LookupVisibility.CanMatchSteamImport
        // already rejected blocked and incomplete candidates), so the match card carries their avatar.
        JsonElement matchAvatar = readyBody.RootElement.GetProperty("matches")[0].GetProperty("avatar");
        Assert.Equal("emoji", matchAvatar.GetProperty("kind").GetString());
        Assert.Equal("🙂", matchAvatar.GetProperty("emoji").GetString());
    }

    [Fact]
    public async Task BlockedAndAbsentLocalCandidates_AreByteEffectAndLimiterEquivalent_D065()
    {
        const string importedSteamId = "same-steam-id";
        FriendEndpointUser actor = await SeedLinkedAsync("blocked-importer");
        FriendEndpointUser candidate = await SeedA030LinkedAsync(importedSteamId);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Blocks.Add(
                Block.Create(
                    actor.Id,
                    candidate.Id,
                    Username.Create(candidate.Username),
                    _factory.Clock));
            await context.SaveChangesAsync();
        }

        _steam.Result = new SteamFriendListSuccess(
            [new SteamFriend(importedSteamId, "Same Persona", "https://cdn.test/same.png")]);
        using FriendEndpointSession session = await SignInAsync(actor.Id);
        GraphSnapshot blockedBaseline = await SnapshotAsync();
        using HttpResponseMessage blockedResponse = await ImportAsync(session);
        byte[] blockedBytes = await blockedResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(blockedBaseline, await SnapshotAsync());

        await using (AsyncServiceScope scope = _configured!.Services.CreateAsyncScope())
        {
            UnlinkSteamResult unlinked = await scope.ServiceProvider
                .GetRequiredService<UnlinkSteamUseCase>()
                .ExecuteAsync(
                    new UnlinkSteamCommand(candidate.Id, UnlinkOrigin.AuthMethod, true),
                    default);
            Assert.True(unlinked.Succeeded);
        }

        GraphSnapshot absentBaseline = await SnapshotAsync();
        using HttpResponseMessage absentResponse = await ImportAsync(session);
        byte[] absentBytes = await absentResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(absentBaseline, await SnapshotAsync());

        Assert.Equal(HttpStatusCode.OK, blockedResponse.StatusCode);
        Assert.Equal(blockedResponse.StatusCode, absentResponse.StatusCode);
        Assert.Equal("application/json", blockedResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            blockedResponse.Content.Headers.ContentType,
            absentResponse.Content.Headers.ContentType);
        Assert.Equal(HeaderSnapshot(blockedResponse), HeaderSnapshot(absentResponse));
        Assert.Equal(blockedBytes, absentBytes);

        using JsonDocument body = JsonDocument.Parse(blockedBytes);
        JsonElement root = body.RootElement;
        string invitationUrl = $"http://localhost:5173/add/{actor.FriendCode}";
        Assert.Equal("zero_matches", root.GetProperty("state").GetString());
        Assert.Equal(invitationUrl, root.GetProperty("invitationUrl").GetString());
        JsonElement unmatched = Assert.Single(root.GetProperty("unmatched").EnumerateArray());
        Assert.Equal(invitationUrl, unmatched.GetProperty("invitationUrl").GetString());
        Assert.Equal(importedSteamId, unmatched.GetProperty("steamId64").GetString());

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

    private static Task<HttpResponseMessage> ImportAsync(FriendEndpointSession session) =>
        session.SendUnsafeAsync(HttpMethod.Post, "/api/v1/friends/imports/steam");

    private static Dictionary<string, string> HeaderSnapshot(
        HttpResponseMessage response) =>
        response.Headers
            .Concat(response.Content.Headers)
            .Where(header => !header.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                header => header.Key,
                header => string.Join(',', header.Value),
                StringComparer.OrdinalIgnoreCase);

    private async Task<GraphSnapshot> SnapshotAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var friendships = await context.Friendships.AsNoTracking().ToArrayAsync();
        var blocks = await context.Blocks.AsNoTracking().ToArrayAsync();
        var notifications = await context.Notifications.AsNoTracking().ToArrayAsync();
        return new GraphSnapshot(
            string.Join('|', friendships
                .OrderBy(friendship => friendship.Id.Value)
                .Select(friendship =>
                    $"{friendship.Id.Value:D}:{friendship.Status}:{friendship.AcceptedAt}")),
            string.Join('|', blocks
                .OrderBy(block => block.Id.Value)
                .Select(block =>
                    $"{block.Id.Value:D}:{block.BlockerId.Value:D}:{block.BlockedId.Value:D}")),
            string.Join('|', notifications
                .OrderBy(notification => notification.Id.Value)
                .Select(notification =>
                    $"{notification.Id.Value:D}:{notification.RecipientUserId.Value:D}:" +
                    $"{notification.ActorUserId?.Value:D}:{notification.Type}")));
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

    private async Task<FriendEndpointUser> SeedA030LinkedAsync(string steamId64)
    {
        FriendEndpointUser user = await FriendEndpointTest.SeedAsync(_factory);
        await using AsyncServiceScope scope = _configured!.Services.CreateAsyncScope();
        ExternalSignInResult linked = await scope.ServiceProvider
            .GetRequiredService<CompleteExternalSignInUseCase>()
            .ExecuteAsync(
                new CompleteExternalSignInCommand(
                    "steam",
                    steamId64,
                    null,
                    false,
                    ExternalStartMode.Link,
                    false,
                    user.Id,
                    user.Id),
                default);
        Assert.True(linked.MethodLinked);
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

    private sealed record GraphSnapshot(
        string Friendships,
        string Blocks,
        string Notifications);
}

internal sealed class MutableSteamFriendListClient : ISteamFriendListClient
{
    internal SteamFriendListResult Result { get; set; } = new SteamFriendListNotConfigured();

    public Task<SteamFriendListResult> GetAsync(
        string steamId64,
        CancellationToken cancellationToken) => Task.FromResult(Result);
}
