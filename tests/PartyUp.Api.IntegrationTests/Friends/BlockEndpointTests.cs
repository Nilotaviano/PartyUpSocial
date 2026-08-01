using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class BlockEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task BlockListAndUnblock_ExposeOnlyOutboundManagementItem()
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser target = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession session = await FriendEndpointTest.SignInAsync(
            _factory,
            actor.Id);

        HttpResponseMessage created = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/blocks",
            JsonContent.Create(new { targetUsername = target.Username }));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Null(created.Headers.Location);
        using JsonDocument item = await FriendEndpointTest.SuccessJsonAsync(created);
        Assert.Equal(3, item.RootElement.EnumerateObject().Count());
        Assert.Equal(target.Username, item.RootElement.GetProperty("username").GetString());
        Guid blockId = item.RootElement.GetProperty("blockId").GetGuid();

        using JsonDocument list = await FriendEndpointTest.SuccessJsonAsync(
            await session.GetAsync("/api/v1/blocks"));
        Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());

        HttpResponseMessage removed = await session.SendUnsafeAsync(
            HttpMethod.Delete,
            $"/api/v1/blocks/{blockId:D}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task BlockSelf_ReturnsDocumentedCode()
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession session = await FriendEndpointTest.SignInAsync(
            _factory,
            actor.Id);

        HttpResponseMessage response = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/blocks",
            JsonContent.Create(new { targetUsername = actor.Username }));

        await FriendEndpointTest.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "block_self",
            "/api/v1/blocks");
    }
}
