using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendActionEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AcceptThenUnfriend_UsesActorOwnedRouteIdentifiers()
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

        using JsonDocument accepted = await FriendEndpointTest.SuccessJsonAsync(
            await addresseeSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/friend-requests/{friendshipId:D}/accept"));
        Assert.Equal("accepted", accepted.RootElement.GetProperty("status").GetString());
        Assert.Equal("accepted", accepted.RootElement.GetProperty("direction").GetString());

        HttpResponseMessage removed = await requesterSession.SendUnsafeAsync(
            HttpMethod.Delete,
            $"/api/v1/friends/{friendshipId:D}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/friend-requests/not-a-guid/accept", "friend_request_not_found")]
    [InlineData("/api/v1/friends/not-a-guid", "friendship_not_found")]
    public async Task MalformedRouteIds_UseDocumentedNotFoundProblem(
        string path,
        string code)
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession session = await FriendEndpointTest.SignInAsync(
            _factory,
            actor.Id);

        HttpResponseMessage response = await session.SendUnsafeAsync(
            path.Contains("/accept", StringComparison.Ordinal)
                ? HttpMethod.Post
                : HttpMethod.Delete,
            path);

        await FriendEndpointTest.AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            code,
            path);
    }
}
