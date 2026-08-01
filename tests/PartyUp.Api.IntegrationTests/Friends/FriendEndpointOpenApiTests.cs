using System.Text.Json;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendEndpointOpenApiTests : IAsyncLifetime, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string[]> Operations =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["GetFriendShare"] = ["200", "401", "403", "429", "500"],
            ["GetUserProfile"] = ["200", "401", "403", "404", "429", "500"],
            ["ResolveFriendCode"] = ["200", "400", "401", "403", "404", "429", "500"],
            ["ListFriends"] = ["200", "401", "403", "429", "500"],
            ["ListFriendRequests"] = ["200", "401", "403", "429", "500"],
            ["SendFriendRequest"] = ["200", "201", "400", "401", "403", "404", "409", "415", "429", "500"],
            ["AcceptFriendRequest"] = ["200", "400", "401", "403", "404", "409", "429", "500"],
            ["RejectFriendRequest"] = ["204", "400", "401", "403", "404", "409", "429", "500"],
            ["CancelFriendRequest"] = ["204", "400", "401", "403", "404", "409", "429", "500"],
            ["Unfriend"] = ["204", "400", "401", "403", "404", "429", "500"],
            ["ListBlocks"] = ["200", "401", "403", "429", "500"],
            ["BlockUser"] = ["201", "400", "401", "403", "404", "415", "429", "500"],
            ["UnblockUser"] = ["204", "400", "401", "403", "404", "429", "500"],
        };
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Document_ContainsExactFriendInventoryAndClosedResolutionSchemas()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        using JsonDocument document = JsonDocument.Parse(
            await client.GetStringAsync("/openapi/v1.json"));
        JsonElement root = document.RootElement;
        Dictionary<string, JsonElement> operations = root.GetProperty("paths")
            .EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Where(operation => operation.Value.TryGetProperty("operationId", out _))
            .ToDictionary(
                operation => operation.Value.GetProperty("operationId").GetString()!,
                operation => operation.Value,
                StringComparer.Ordinal);

        foreach ((string operationId, string[] expectedStatuses) in Operations)
        {
            JsonElement operation = operations[operationId];
            Assert.Equal(
                expectedStatuses.Order(StringComparer.Ordinal),
                operation.GetProperty("responses").EnumerateObject()
                    .Select(response => response.Name)
                    .Order(StringComparer.Ordinal));
            Assert.Single(operation.GetProperty("security").EnumerateArray());
            Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
        }

        JsonElement schemas = root.GetProperty("components").GetProperty("schemas");
        JsonElement profile = schemas.GetProperty("ProfileViewResponse");
        Assert.Equal(2, profile.GetProperty("oneOf").GetArrayLength());
        Assert.False(profile.TryGetProperty("anyOf", out _));
        Assert.Equal(
            ["profile"],
            schemas.GetProperty("UsernameProfileResolutionResponse")
                .GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.False(
            schemas.GetProperty("UsernameProfileResolutionResponse")
                .GetProperty("additionalProperties")
                .GetBoolean());
        Assert.Equal(
            ["profile"],
            schemas.GetProperty("FriendCodeResolutionResponse")
                .GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.False(
            schemas.GetProperty("FriendCodeResolutionResponse")
                .GetProperty("additionalProperties")
                .GetBoolean());
        Assert.Equal(
            ["type", "title", "status", "instance", "code", "traceId"],
            schemas.GetProperty("PartyUpProblem").GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()));
    }
}
