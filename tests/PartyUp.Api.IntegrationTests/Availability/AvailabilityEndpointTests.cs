using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Net.Http.Headers;
using NodaTime;
using NodaTime.Text;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md P5-HTTP-01/§8/§8.1: the <c>/me/availability/**</c> HTTP surface — inventory,
/// antiforgery/onboarding gates, the 404-not-403 non-owner rule (D-009), the closed occurrence-edit
/// scope union and its §5.2 branches over HTTP (D-143).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task InventoryHasExactOperationIdsAndSchemas()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement paths = document.RootElement.GetProperty("paths");

        Dictionary<string, Dictionary<string, string>> expected = new()
        {
            ["/api/v1/me/availability"] = new() { ["get"] = ApiOperationIds.GetMyAvailability },
            ["/api/v1/me/availability/blocks"] = new() { ["post"] = ApiOperationIds.CreateAvailabilityBlock },
            ["/api/v1/me/availability/blocks/{blockId}"] = new()
            {
                ["patch"] = ApiOperationIds.EditAvailabilityBlock,
                ["delete"] = ApiOperationIds.DeleteAvailabilityBlock,
            },
            ["/api/v1/me/availability/series"] = new() { ["post"] = ApiOperationIds.CreateAvailabilitySeries },
            ["/api/v1/me/availability/series/{seriesId}"] = new()
            {
                ["delete"] = ApiOperationIds.DeleteAvailabilitySeries,
            },
            ["/api/v1/me/availability/series/{seriesId}/occurrences/{occurrenceDate}"] = new()
            {
                ["patch"] = ApiOperationIds.EditAvailabilityOccurrence,
                ["delete"] = ApiOperationIds.DeleteAvailabilityOccurrence,
            },
            ["/api/v1/availability/overlaps"] = new() { ["get"] = ApiOperationIds.FindOverlapWindows },
            ["/api/v1/availability/suggestions"] = new() { ["get"] = ApiOperationIds.GetHomeSuggestions },
            ["/api/v1/calendar"] = new() { ["get"] = ApiOperationIds.GetCalendarView },
            ["/api/v1/me/actionable"] = new() { ["get"] = ApiOperationIds.GetActionableInbox },
        };

        foreach ((string path, Dictionary<string, string> methods) in expected)
        {
            Assert.True(paths.TryGetProperty(path, out JsonElement pathItem), $"Missing path {path}");

            string[] httpVerbs = ["get", "post", "put", "patch", "delete"];
            string[] actualMethods = pathItem.EnumerateObject()
                .Select(p => p.Name)
                .Where(name => httpVerbs.Contains(name))
                .Order()
                .ToArray();
            Assert.Equal(methods.Keys.Order().ToArray(), actualMethods);

            foreach ((string method, string operationId) in methods)
            {
                string actualOperationId = pathItem.GetProperty(method).GetProperty("operationId").GetString()!;
                Assert.Equal(operationId, actualOperationId);
            }
        }
    }

    [Fact]
    public async Task UnsafeRoutesRequireAntiforgeryAndOnboarding()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"avail-af-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, ownerId);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/availability/blocks")
        {
            Content = JsonContent.Create(new
            {
                startTime = Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                endTime = Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1) + Duration.FromHours(1)),
            }),
        };
        request.Headers.Add(HeaderNames.Cookie, session.AuthCookie);
        HttpResponseMessage response = await session.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // No auth cookie at all: RequireOnboarded rejects before the handler runs.
        using HttpClient anonymous = _factory.CreateClient();
        HttpResponseMessage unauthenticated = await anonymous.GetAsync("/api/v1/me/availability?from=x&to=y");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task NonOwnerTargetsAre404NotForbidden_D009()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"avail-owner-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"avail-stranger-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, ownerId);
        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);

        Instant start = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await ownerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/me/availability/blocks",
            JsonContent.Create(new { startTime = Iso(start), endTime = Iso(start + Duration.FromHours(1)) }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid blockId = body.RootElement.GetProperty("blockId").GetGuid();

        HttpResponseMessage strangerEdit = await strangerSession.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/blocks/{blockId:D}",
            JsonContent.Create(new { startTime = Iso(start), endTime = Iso(start + Duration.FromHours(2)) }));
        await SessionEndpointTestSupport.AssertProblemAsync(
            strangerEdit, HttpStatusCode.NotFound, "availability_not_found");

        // The owner's own edit on the same block still works — proving the 404 above was identity, not shape.
        HttpResponseMessage ownerEdit = await ownerSession.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/blocks/{blockId:D}",
            JsonContent.Create(new { startTime = Iso(start), endTime = Iso(start + Duration.FromHours(2)) }));
        Assert.Equal(HttpStatusCode.OK, ownerEdit.StatusCode);

        // A stranger's delete on the owner's block is the shipped idempotent no-op — 204, not 404 —
        // because delete never discloses existence either way (§8).
        HttpResponseMessage strangerDelete = await strangerSession.SendUnsafeAsync(
            HttpMethod.Delete, $"/api/v1/me/availability/blocks/{blockId:D}");
        Assert.Equal(HttpStatusCode.NoContent, strangerDelete.StatusCode);
    }

    [Fact]
    public async Task OccurrenceScopeIsRequiredAndClosed_D143()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"avail-scope-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, ownerId);

        LocalDate effectiveFrom = AnchorDate();
        (Guid seriesId, string occurrenceDate) = await CreateSeriesAsync(session, effectiveFrom);

        HttpResponseMessage missingScope = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDate}",
            JsonContent.Create(new { startTime = Iso(_factory.Clock.GetCurrentInstant()) }));
        await SessionEndpointTestSupport.AssertProblemAsync(
            missingScope, HttpStatusCode.BadRequest, "availability_validation");

        HttpResponseMessage unknownScope = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDate}",
            JsonContent.Create(new { scope = "everything" }));
        await SessionEndpointTestSupport.AssertProblemAsync(
            unknownScope, HttpStatusCode.BadRequest, "availability_validation");
    }

    /// <summary>
    /// round-1 fix (F7): the request union is closed PER ARM, not just on <c>scope</c> — a
    /// <c>thisAndFuture</c>-only field on an <c>onlyThis</c> body (and vice versa) is a validation
    /// error, never silently ignored.
    /// </summary>
    [Fact]
    public async Task EditOccurrenceRequestRejectsCrossArmFields_F7()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"avail-crossarm-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, ownerId);

        LocalDate effectiveFrom = AnchorDate();
        (Guid seriesId, string occurrenceDate) = await CreateSeriesAsync(session, effectiveFrom);
        Instant replacementStart = effectiveFrom.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant()
            + Duration.FromHours(20);

        // onlyThis + thisAndFuture-only fields (dayOfWeek, durationMinutes).
        HttpResponseMessage onlyThisWithDayOfWeek = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDate}",
            JsonContent.Create(new
            {
                scope = "onlyThis",
                startTime = Iso(replacementStart),
                endTime = Iso(replacementStart + Duration.FromHours(1)),
                dayOfWeek = 3,
            }));
        await SessionEndpointTestSupport.AssertProblemAsync(
            onlyThisWithDayOfWeek, HttpStatusCode.BadRequest, "availability_validation");

        HttpResponseMessage onlyThisWithDurationMinutes = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDate}",
            JsonContent.Create(new
            {
                scope = "onlyThis",
                startTime = Iso(replacementStart),
                endTime = Iso(replacementStart + Duration.FromHours(1)),
                durationMinutes = 60,
            }));
        await SessionEndpointTestSupport.AssertProblemAsync(
            onlyThisWithDurationMinutes, HttpStatusCode.BadRequest, "availability_validation");

        // thisAndFuture + onlyThis-only field (endTime).
        HttpResponseMessage thisAndFutureWithEndTime = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDate}",
            JsonContent.Create(new
            {
                scope = "thisAndFuture",
                dayOfWeek = (int)effectiveFrom.DayOfWeek,
                startTime = "19:00",
                durationMinutes = 60,
                endTime = Iso(replacementStart + Duration.FromHours(1)),
            }));
        await SessionEndpointTestSupport.AssertProblemAsync(
            thisAndFutureWithEndTime, HttpStatusCode.BadRequest, "availability_validation");

        // thisAndFuture + an Instant-formatted startTime (onlyThis's shape, not thisAndFuture's
        // "HH:mm" LocalTime) — already rejected because it fails LocalTime parsing, proven here.
        HttpResponseMessage thisAndFutureWithInstantStartTime = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDate}",
            JsonContent.Create(new
            {
                scope = "thisAndFuture",
                dayOfWeek = (int)effectiveFrom.DayOfWeek,
                startTime = Iso(replacementStart),
                durationMinutes = 60,
            }));
        await SessionEndpointTestSupport.AssertProblemAsync(
            thisAndFutureWithInstantStartTime, HttpStatusCode.BadRequest, "availability_validation");
    }

    [Fact]
    public async Task OnlyThisReturnsTheMaterializedBlockAndThisAndFutureReturnsBothSeries_D143()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"avail-branch-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, ownerId);

        LocalDate effectiveFrom = AnchorDate();
        (Guid onlyThisSeriesId, string onlyThisDate) = await CreateSeriesAsync(session, effectiveFrom);
        Instant replacementStart = effectiveFrom.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant()
            + Duration.FromHours(20);
        HttpResponseMessage onlyThisResponse = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{onlyThisSeriesId:D}/occurrences/{onlyThisDate}",
            JsonContent.Create(new
            {
                scope = "onlyThis",
                startTime = Iso(replacementStart),
                endTime = Iso(replacementStart + Duration.FromHours(1)),
            }));
        using JsonDocument onlyThisBody = await SessionEndpointTestSupport.SuccessJsonAsync(onlyThisResponse);
        Assert.Equal("onlyThis", onlyThisBody.RootElement.GetProperty("scope").GetString());
        Assert.True(onlyThisBody.RootElement.TryGetProperty("block", out JsonElement block));
        Assert.True(block.TryGetProperty("blockId", out _));
        Assert.False(onlyThisBody.RootElement.TryGetProperty("original", out _));
        Assert.False(onlyThisBody.RootElement.TryGetProperty("successor", out _));

        (Guid splitSeriesId, string splitDate) = await CreateSeriesAsync(session, effectiveFrom.PlusDays(7));
        HttpResponseMessage splitResponse = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{splitSeriesId:D}/occurrences/{splitDate}",
            JsonContent.Create(new
            {
                scope = "thisAndFuture",
                dayOfWeek = (int)effectiveFrom.PlusDays(7).DayOfWeek,
                startTime = "19:00",
                durationMinutes = 90,
            }));
        using JsonDocument splitBody = await SessionEndpointTestSupport.SuccessJsonAsync(splitResponse);
        Assert.Equal("thisAndFuture", splitBody.RootElement.GetProperty("scope").GetString());
        Assert.True(splitBody.RootElement.TryGetProperty("successor", out JsonElement successor));
        Assert.True(successor.TryGetProperty("seriesId", out _));
        // The split lands exactly on the occurrence date: the original is consumed whole and dropped.
        Assert.Equal(JsonValueKind.Null, splitBody.RootElement.GetProperty("original").ValueKind);
    }

    [Fact]
    public async Task RepeatedOnlyThisEditReturnsTheSameBlockIdAndAfterADeleteIs404_D143()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"avail-repeat-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, ownerId);

        LocalDate effectiveFrom = AnchorDate();
        (Guid seriesId, string occurrenceDate) = await CreateSeriesAsync(session, effectiveFrom);
        Instant baseStart = effectiveFrom.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant() + Duration.FromHours(20);

        HttpResponseMessage firstEdit = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDate}",
            JsonContent.Create(new
            {
                scope = "onlyThis",
                startTime = Iso(baseStart),
                endTime = Iso(baseStart + Duration.FromHours(1)),
            }));
        using JsonDocument firstBody = await SessionEndpointTestSupport.SuccessJsonAsync(firstEdit);
        Guid firstBlockId = firstBody.RootElement.GetProperty("block").GetProperty("blockId").GetGuid();

        HttpResponseMessage secondEdit = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDate}",
            JsonContent.Create(new
            {
                scope = "onlyThis",
                startTime = Iso(baseStart + Duration.FromMinutes(30)),
                endTime = Iso(baseStart + Duration.FromHours(2)),
            }));
        using JsonDocument secondBody = await SessionEndpointTestSupport.SuccessJsonAsync(secondEdit);
        Assert.Equal(HttpStatusCode.OK, secondEdit.StatusCode);
        Guid secondBlockId = secondBody.RootElement.GetProperty("block").GetProperty("blockId").GetGuid();
        Assert.Equal(firstBlockId, secondBlockId);

        HttpResponseMessage deleted = await session.SendUnsafeAsync(
            HttpMethod.Delete, $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDate}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        HttpResponseMessage afterDelete = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDate}",
            JsonContent.Create(new
            {
                scope = "onlyThis",
                startTime = Iso(baseStart),
                endTime = Iso(baseStart + Duration.FromHours(1)),
            }));
        await SessionEndpointTestSupport.AssertProblemAsync(
            afterDelete, HttpStatusCode.NotFound, "occurrence_not_found");
    }

    [Fact]
    public async Task ThisAndFutureAcceptsANewDayOfWeekOverHttp_D143()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"avail-weekday-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, ownerId);

        LocalDate effectiveFrom = AnchorDate();
        (Guid seriesId, string occurrenceDate) = await CreateSeriesAsync(session, effectiveFrom);

        // DOMAIN_MODEL.md:196: the successor's dayOfWeek may differ from the split occurrence's weekday.
        IsoDayOfWeek newWeekday = effectiveFrom.DayOfWeek == IsoDayOfWeek.Monday
            ? IsoDayOfWeek.Wednesday
            : IsoDayOfWeek.Monday;

        HttpResponseMessage response = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDate}",
            JsonContent.Create(new
            {
                scope = "thisAndFuture",
                dayOfWeek = (int)newWeekday,
                startTime = "20:00",
                durationMinutes = 45,
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(response);
        JsonElement successor = body.RootElement.GetProperty("successor");
        Assert.Equal((int)newWeekday, successor.GetProperty("dayOfWeek").GetInt32());
        Assert.Equal(45, successor.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("20:00", successor.GetProperty("startTime").GetString());
    }

    [Fact]
    public async Task RangeBeyondNinetyTwoDaysIsRefusedOnCalendarAndMyAvailability_D108()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"avail-range-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, ownerId);

        Instant from = _factory.Clock.GetCurrentInstant();
        Instant to = from + Duration.FromDays(93);

        HttpResponseMessage myAvailability = await session.GetAsync(
            $"/api/v1/me/availability?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}");
        await SessionEndpointTestSupport.AssertProblemAsync(
            myAvailability, HttpStatusCode.BadRequest, "range_too_large");

        HttpResponseMessage calendar = await session.GetAsync(
            $"/api/v1/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}");
        await SessionEndpointTestSupport.AssertProblemAsync(
            calendar, HttpStatusCode.BadRequest, "range_too_large");

        // Sanity: exactly 92 days is allowed (§12-27's inclusive bound).
        Instant boundedTo = from + Duration.FromDays(92);
        HttpResponseMessage allowed = await session.GetAsync(
            $"/api/v1/me/availability?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(boundedTo))}");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // ---- shared helpers ---------------------------------------------------------------------------

    /// <summary>A date at least a week past the factory clock, so every occurrence used in these tests
    /// is comfortably in the future.</summary>
    private LocalDate AnchorDate() =>
        _factory.Clock.GetCurrentInstant().InUtc().Date.PlusDays(10);

    private static async Task<(Guid SeriesId, string OccurrenceDate)> CreateSeriesAsync(
        SessionHttpSession session,
        LocalDate effectiveFrom,
        string startTime = "18:00",
        int durationMinutes = 60)
    {
        string effectiveFromText = LocalDatePattern.Iso.Format(effectiveFrom);
        HttpResponseMessage response = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/me/availability/series",
            JsonContent.Create(new
            {
                dayOfWeek = (int)effectiveFrom.DayOfWeek,
                startTime,
                durationMinutes,
                effectiveFrom = effectiveFromText,
            }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(response);
        return (body.RootElement.GetProperty("seriesId").GetGuid(), effectiveFromText);
    }

    private static string Iso(Instant instant) => InstantPattern.ExtendedIso.Format(instant);
}
