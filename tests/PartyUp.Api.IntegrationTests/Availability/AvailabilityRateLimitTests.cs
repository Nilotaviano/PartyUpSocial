using System.Net;
using System.Net.Http.Json;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Api.Security;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md A-079: five new User-partitioned rate-limit dimensions, assigned exhaustively so
/// every §8 route carries exactly one.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityRateLimitTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void EveryPhase5RouteCarriesItsDeclaredDimensionAndPartitionsByUser_A079()
    {
        AssertDimension(
            "availability-write",
            60,
            [
                ApiOperationIds.CreateAvailabilityBlock,
                ApiOperationIds.EditAvailabilityBlock,
                ApiOperationIds.DeleteAvailabilityBlock,
                ApiOperationIds.CreateAvailabilitySeries,
                ApiOperationIds.DeleteAvailabilitySeries,
                ApiOperationIds.EditAvailabilityOccurrence,
                ApiOperationIds.DeleteAvailabilityOccurrence,
            ]);
        AssertDimension(
            "overlap-search", 30, [ApiOperationIds.FindOverlapWindows, ApiOperationIds.GetHomeSuggestions]);
        AssertDimension(
            "calendar-read", 120, [ApiOperationIds.GetCalendarView, ApiOperationIds.GetMyAvailability]);
        AssertDimension("home-read", 120, [ApiOperationIds.GetActionableInbox]);
        AssertDimension("session-list-read", 120, [ApiOperationIds.ListSessions]);

        // Every §8 route resolves to exactly one of the five dimensions above.
        string[] phase5Operations =
        [
            ApiOperationIds.GetMyAvailability,
            ApiOperationIds.CreateAvailabilityBlock,
            ApiOperationIds.EditAvailabilityBlock,
            ApiOperationIds.DeleteAvailabilityBlock,
            ApiOperationIds.CreateAvailabilitySeries,
            ApiOperationIds.DeleteAvailabilitySeries,
            ApiOperationIds.EditAvailabilityOccurrence,
            ApiOperationIds.DeleteAvailabilityOccurrence,
            ApiOperationIds.FindOverlapWindows,
            ApiOperationIds.GetHomeSuggestions,
            ApiOperationIds.GetCalendarView,
            ApiOperationIds.ListSessions,
            ApiOperationIds.GetActionableInbox,
        ];
        string[] phase5DimensionNames =
            ["availability-write", "overlap-search", "calendar-read", "home-read", "session-list-read"];
        foreach (string operationId in phase5Operations)
        {
            RateLimitDimension[] matches = [.. RateLimitPolicies.Dimensions
                .Where(d => phase5DimensionNames.Contains(d.Name) && d.OperationIds.Contains(operationId))];
            Assert.True(
                matches.Length == 1,
                $"{operationId} must carry exactly one of the five A-079 dimensions, found {matches.Length}.");
        }
    }

    [Fact]
    public async Task AvailabilityWriteDimensionPartitionsByUser()
    {
        UserId first = await _factory.SeedUserAsync(email: $"rl-avail-first-{Guid.NewGuid():N}@example.test");
        UserId second = await _factory.SeedUserAsync(email: $"rl-avail-second-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession firstSession = await SessionEndpointTestSupport.SignInAsync(_factory, first);
        using SessionHttpSession secondSession = await SessionEndpointTestSupport.SignInAsync(_factory, second);

        RateLimitDimension dimension = RateLimitPolicies.Dimensions.Single(d => d.Name == "availability-write");

        HttpStatusCode? rejected = null;
        for (int i = 0; i < dimension.PermitLimit + 1; i++)
        {
            Instant start = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1) + Duration.FromMinutes(15 * i);
            HttpResponseMessage response = await firstSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/me/availability/blocks",
                JsonContent.Create(new
                {
                    startTime = NodaTime.Text.InstantPattern.ExtendedIso.Format(start),
                    endTime = NodaTime.Text.InstantPattern.ExtendedIso.Format(start + Duration.FromMinutes(15)),
                }));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response.StatusCode;
                break;
            }

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected);

        Instant secondStart = _factory.Clock.GetCurrentInstant() + Duration.FromDays(2);
        HttpResponseMessage secondUserResponse = await secondSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/me/availability/blocks",
            JsonContent.Create(new
            {
                startTime = NodaTime.Text.InstantPattern.ExtendedIso.Format(secondStart),
                endTime = NodaTime.Text.InstantPattern.ExtendedIso.Format(secondStart + Duration.FromMinutes(15)),
            }));
        Assert.Equal(HttpStatusCode.Created, secondUserResponse.StatusCode);
    }

    private static void AssertDimension(string name, int permitLimit, string[] operationIds)
    {
        RateLimitDimension dimension = RateLimitPolicies.Dimensions.Single(d => d.Name == name);
        Assert.Equal(permitLimit, dimension.PermitLimit);
        Assert.Equal(RateLimitPartitionKind.User, dimension.PartitionKind);
        Assert.Equal(Duration.FromMinutes(1).ToTimeSpan(), dimension.Window);
        Assert.Equal(operationIds.Order(StringComparer.Ordinal).ToArray(), dimension.OperationIds.Order(StringComparer.Ordinal).ToArray());
    }
}
