using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PartyUp.Domain.Friendships;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendRequestEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task SendAndList_ReturnPendingResourceWithoutLocation()
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser target = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession session = await FriendEndpointTest.SignInAsync(
            _factory,
            actor.Id);

        HttpResponseMessage created = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = target.Username }));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Null(created.Headers.Location);
        using JsonDocument resource = await FriendEndpointTest.SuccessJsonAsync(created);
        Assert.Equal("pending", resource.RootElement.GetProperty("status").GetString());
        Assert.Equal("outgoing", resource.RootElement.GetProperty("direction").GetString());
        Assert.Equal(
            target.Username,
            resource.RootElement.GetProperty("counterpartUsername").GetString());

        using JsonDocument listed = await FriendEndpointTest.SuccessJsonAsync(
            await session.GetAsync("/api/v1/friend-requests"));
        Assert.Single(listed.RootElement.GetProperty("outgoing").EnumerateArray());
        Assert.Empty(listed.RootElement.GetProperty("incoming").EnumerateArray());
    }

    [Fact]
    public async Task CrossedSend_Returns200WithOneAcceptedPair_D015()
    {
        FriendEndpointUser requester = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser addressee = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession requesterSession = await FriendEndpointTest.SignInAsync(
            _factory,
            requester.Id);
        using FriendEndpointSession addresseeSession = await FriendEndpointTest.SignInAsync(
            _factory,
            addressee.Id);
        using JsonDocument pending = await FriendEndpointTest.SuccessJsonAsync(
            await requesterSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/friend-requests",
                JsonContent.Create(new { targetUsername = addressee.Username })));
        Guid friendshipId = pending.RootElement.GetProperty("friendshipId").GetGuid();

        HttpResponseMessage crossed = await addresseeSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = requester.Username }));

        Assert.Equal(HttpStatusCode.OK, crossed.StatusCode);
        using JsonDocument accepted = await FriendEndpointTest.SuccessJsonAsync(crossed);
        Assert.Equal(friendshipId, accepted.RootElement.GetProperty("friendshipId").GetGuid());
        Assert.Equal("accepted", accepted.RootElement.GetProperty("status").GetString());
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Friendship row = await context.Friendships.SingleAsync();
        Assert.Equal(friendshipId, row.Id.Value);
        Assert.Equal(FriendshipStatus.Accepted, row.Status);
    }

    [Fact]
    public async Task CrossedSend_CreatesNoAcceptanceNotification_D105()
    {
        FriendEndpointUser requester = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser addressee = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession requesterSession = await FriendEndpointTest.SignInAsync(
            _factory,
            requester.Id);
        using FriendEndpointSession addresseeSession = await FriendEndpointTest.SignInAsync(
            _factory,
            addressee.Id);
        HttpResponseMessage pending = await requesterSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = addressee.Username }));
        Assert.Equal(HttpStatusCode.Created, pending.StatusCode);
        await using AsyncServiceScope beforeScope = _factory.Services.CreateAsyncScope();
        int before = await beforeScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Notifications.CountAsync();
        Assert.Equal(1, before);

        HttpResponseMessage crossed = await addresseeSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = requester.Username }));

        Assert.Equal(HttpStatusCode.OK, crossed.StatusCode);
        await using AsyncServiceScope afterScope = _factory.Services.CreateAsyncScope();
        int after = await afterScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Notifications.CountAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task PendingListsHaveExactUsernameFriendshipIdAndRequestedAtShape_D068()
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser outgoingTarget = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser incomingSource = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession actorSession = await FriendEndpointTest.SignInAsync(
            _factory,
            actor.Id);
        using FriendEndpointSession sourceSession = await FriendEndpointTest.SignInAsync(
            _factory,
            incomingSource.Id);
        Assert.Equal(
            HttpStatusCode.Created,
            (await actorSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/friend-requests",
                JsonContent.Create(new { targetUsername = outgoingTarget.Username }))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await sourceSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/friend-requests",
                JsonContent.Create(new { targetUsername = actor.Username }))).StatusCode);

        using JsonDocument listed = await FriendEndpointTest.SuccessJsonAsync(
            await actorSession.GetAsync("/api/v1/friend-requests"));

        Assert.Equal(
            ["incoming", "outgoing"],
            listed.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        JsonElement incoming = Assert.Single(
            listed.RootElement.GetProperty("incoming").EnumerateArray());
        JsonElement outgoing = Assert.Single(
            listed.RootElement.GetProperty("outgoing").EnumerateArray());
        string[] allowed = ["friendshipId", "requestedAt", "username"];
        Assert.Equal(
            allowed,
            incoming.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            allowed,
            outgoing.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(incomingSource.Username, incoming.GetProperty("username").GetString());
        Assert.Equal(outgoingTarget.Username, outgoing.GetProperty("username").GetString());
    }

    [Fact]
    public async Task StalePendingAction_ReturnsFriendRequestNotPending_D015()
    {
        FriendEndpointUser requester = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser addressee = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession requesterSession = await FriendEndpointTest.SignInAsync(
            _factory,
            requester.Id);
        using JsonDocument pending = await FriendEndpointTest.SuccessJsonAsync(
            await requesterSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/friend-requests",
                JsonContent.Create(new { targetUsername = addressee.Username })));
        Guid friendshipId = pending.RootElement.GetProperty("friendshipId").GetGuid();
        using FriendEndpointSession addresseeSession = await FriendEndpointTest.SignInAsync(
            _factory,
            addressee.Id);
        string path = $"/api/v1/friend-requests/{friendshipId:D}/reject";
        Assert.Equal(
            HttpStatusCode.OK,
            (await addresseeSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/friend-requests/{friendshipId:D}/accept")).StatusCode);

        await FriendEndpointTest.AssertProblemAsync(
            await addresseeSession.SendUnsafeAsync(HttpMethod.Post, path),
            HttpStatusCode.Conflict,
            "friend_request_not_pending",
            path);
    }

    [Fact]
    public async Task SendToSelf_ReturnsDocumentedCode()
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession session = await FriendEndpointTest.SignInAsync(
            _factory,
            actor.Id);

        HttpResponseMessage response = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = actor.Username }));

        await FriendEndpointTest.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "friend_request_self",
            "/api/v1/friend-requests");
    }
}
