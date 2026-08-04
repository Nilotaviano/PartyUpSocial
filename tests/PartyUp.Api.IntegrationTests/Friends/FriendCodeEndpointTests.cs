using System.Net;
using System.Text.Json;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendCodeEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Resolve_ReturnsDistinctProfileOnlyEnvelope()
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser target = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession session = await FriendEndpointTest.SignInAsync(
            _factory,
            actor.Id);

        using JsonDocument response = await FriendEndpointTest.SuccessJsonAsync(
            await session.GetAsync($"/api/v1/friend-codes/{target.FriendCode}"));

        Assert.Single(response.RootElement.EnumerateObject());
        JsonElement profile = response.RootElement.GetProperty("profile");
        Assert.Equal(
            ["disclosure", "relationship", "username"],
            profile.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(target.Username, profile.GetProperty("username").GetString());
        Assert.False(profile.TryGetProperty("friendCode", out _));
        Assert.False(profile.TryGetProperty("addUrl", out _));
    }

    [Fact]
    public async Task Resolve_InvalidNormalizedCode_ReturnsExactProblem()
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession session = await FriendEndpointTest.SignInAsync(
            _factory,
            actor.Id);

        HttpResponseMessage response = await session.GetAsync("/api/v1/friend-codes/NOT-VALID-I");

        await FriendEndpointTest.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "friend_code_invalid",
            "/api/v1/friend-codes/NOT-VALID-I");
    }
}
