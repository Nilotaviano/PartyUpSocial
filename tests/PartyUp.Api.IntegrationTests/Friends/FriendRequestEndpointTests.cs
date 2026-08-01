using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
