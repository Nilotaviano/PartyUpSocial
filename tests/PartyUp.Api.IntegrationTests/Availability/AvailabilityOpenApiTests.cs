using System.Text.Json;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md P5-HTTP-01/A-071: operation ids stay globally unique with the Phase 5 surface
/// added, and every new problem code's documented status is present on the routes §8 assigns it to.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityOpenApiTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task OperationIdsAreGloballyUniqueAndEveryProblemCodeMaps_A071()
    {
        using JsonDocument document = await FetchDocumentAsync();
        JsonElement paths = document.RootElement.GetProperty("paths");

        List<string> operationIds = [];
        foreach (JsonProperty path in paths.EnumerateObject())
        {
            foreach (JsonProperty operation in path.Value.EnumerateObject())
            {
                if (operation.Value.TryGetProperty("operationId", out JsonElement operationId))
                {
                    operationIds.Add(operationId.GetString()!);
                }
            }
        }

        Assert.Equal(operationIds.Count, operationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(ApiOperationIds.GetMyAvailability, operationIds);
        Assert.Contains(ApiOperationIds.CreateAvailabilityBlock, operationIds);
        Assert.Contains(ApiOperationIds.EditAvailabilityOccurrence, operationIds);
        Assert.Contains(ApiOperationIds.GetCalendarView, operationIds);
        Assert.Contains(ApiOperationIds.GetActionableInbox, operationIds);
        Assert.Contains(ApiOperationIds.FindOverlapWindows, operationIds);
        Assert.Contains(ApiOperationIds.GetHomeSuggestions, operationIds);

        // §8's closed problem-code inventory: five new codes, each with its documented status present
        // on the operations that can emit it.
        AssertStatusDocumented(paths, "/api/v1/me/availability", "get", 400); // invalid_range / range_too_large
        AssertStatusDocumented(paths, "/api/v1/me/availability/blocks", "post", 400); // availability_validation
        AssertStatusDocumented(paths, "/api/v1/me/availability/blocks/{blockId}", "patch", 404); // availability_not_found
        AssertStatusDocumented(paths, "/api/v1/me/availability/blocks/{blockId}", "patch", 400);
        AssertStatusDocumented(
            paths, "/api/v1/me/availability/series/{seriesId}/occurrences/{occurrenceDate}", "patch", 404); // occurrence_not_found
        AssertStatusDocumented(
            paths, "/api/v1/me/availability/series/{seriesId}/occurrences/{occurrenceDate}", "patch", 400);
        AssertStatusDocumented(paths, "/api/v1/availability/overlaps", "get", 400); // invalid_range / range_too_large
        AssertStatusDocumented(paths, "/api/v1/calendar", "get", 400);
    }

    private static void AssertStatusDocumented(JsonElement paths, string path, string method, int status)
    {
        JsonElement responses = paths.GetProperty(path).GetProperty(method).GetProperty("responses");
        Assert.True(
            responses.TryGetProperty(status.ToString(System.Globalization.CultureInfo.InvariantCulture), out _),
            $"{method.ToUpperInvariant()} {path} must document status {status}.");
    }

    private async Task<JsonDocument> FetchDocumentAsync()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
