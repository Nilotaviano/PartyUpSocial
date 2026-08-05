using System.Text.Json;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// P4-HTTP-01's OpenAPI inventory assertions (PHASE4_DESIGN.md §8.2): globally unique operation ids,
/// no token-bearing parameter, and the A-064 exchange route's absence from the generated document.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionOpenApiTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task DocumentContainsNoBearerTokenParameter()
    {
        using JsonDocument document = await FetchDocumentAsync();
        foreach (JsonProperty path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (JsonProperty operation in path.Value.EnumerateObject())
            {
                if (!operation.Value.TryGetProperty("parameters", out JsonElement parameters))
                {
                    continue;
                }

                foreach (JsonElement parameter in parameters.EnumerateArray())
                {
                    string? name = parameter.GetProperty("name").GetString();
                    Assert.False(
                        string.Equals(name, "token", StringComparison.OrdinalIgnoreCase),
                        $"Operation {path.Name} carries a bearer token parameter.");
                }
            }
        }
    }

    [Fact]
    public async Task OperationIdsAreGloballyUnique_A071()
    {
        using JsonDocument document = await FetchDocumentAsync();
        List<string> operationIds = [];
        foreach (JsonProperty path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (JsonProperty operation in path.Value.EnumerateObject())
            {
                if (operation.Value.TryGetProperty("operationId", out JsonElement operationId))
                {
                    operationIds.Add(operationId.GetString()!);
                }
            }
        }

        Assert.Contains("GetSessionDetail", operationIds);
        // A-071: GetSession is already taken by /api/v1/auth/session, so the session read is renamed.
        Assert.DoesNotContain(operationIds, id => id == "GetSession" && operationIds.Count(x => x == "GetSession") > 1);
        Assert.Equal(operationIds.Count, operationIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task GuestExchangeRouteIsAbsentFromTheDocument_A064()
    {
        using JsonDocument document = await FetchDocumentAsync();
        JsonElement paths = document.RootElement.GetProperty("paths");
        foreach (JsonProperty path in paths.EnumerateObject())
        {
            Assert.False(
                path.Name.StartsWith("/g/", StringComparison.Ordinal) || path.Name == "/g",
                $"Unexpected /g path in OpenAPI document: {path.Name}");
        }
    }

    private async Task<JsonDocument> FetchDocumentAsync()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
