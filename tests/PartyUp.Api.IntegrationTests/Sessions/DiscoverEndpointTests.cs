using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// PHASE5_DESIGN.md A-077 (c)/(f), P5-VIS-01 (e): <c>GET /sessions?scope=discover</c> over HTTP — the
/// same fail-closed 404/empty-feed shape for hidden versus missing, and the A-072 discover cursor
/// (ascending <c>start_time</c> then <c>id</c>, rejected across lists).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DiscoverEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task HiddenAndMissingAreEquivalent_D007_D019()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"discover-org-{Guid.NewGuid():N}@example.test");
        UserId viewerId = await _factory.SeedUserAsync(email: $"discover-viewer-{Guid.NewGuid():N}@example.test");
        UserId fillerId = await _factory.SeedUserAsync(email: $"discover-filler-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, fillerId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        // A private session the viewer shares no path to: hidden, never in Discover.
        HttpResponseMessage privateCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Hidden from discover",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                isPrivate = true,
                minAttendance = 1,
                inviteeUserIds = new[] { fillerId.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, privateCreated.StatusCode);
        Guid privateSessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(privateCreated))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        using JsonDocument emptyPage = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync("/api/v1/sessions?scope=discover"));
        Guid[] visibleIds = emptyPage.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();

        // The hidden session never appears — the same feed shape as a world with no session at all;
        // Discover has no per-id lookup to compare a 404 against, so equivalence is "never surfaced".
        Assert.DoesNotContain(privateSessionId, visibleIds);
    }

    [Fact]
    public async Task PagesByStartTimeThenIdAndRejectsAnUpcomingCursor_D149_A072()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"discover-page-org-{Guid.NewGuid():N}@example.test");
        UserId fillerId = await _factory.SeedUserAsync(email: $"discover-page-filler-{Guid.NewGuid():N}@example.test");
        UserId viewerId = await _factory.SeedUserAsync(email: $"discover-page-viewer-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, fillerId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, viewerId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        Instant earlier = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        Instant later = _factory.Clock.GetCurrentInstant() + Duration.FromDays(2);
        Guid earlierId = await CreateDiscoverableAsync(organizerSession, "Discover ordering — earlier", earlier, fillerId);
        Guid laterId = await CreateDiscoverableAsync(organizerSession, "Discover ordering — later", later, fillerId);

        using JsonDocument page = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync("/api/v1/sessions?scope=discover"));
        JsonElement[] items = [.. page.RootElement.GetProperty("items").EnumerateArray()];
        Guid[] orderedIds = [.. items.Select(item => item.GetProperty("id").GetGuid())];
        Assert.Contains(earlierId, orderedIds);
        Assert.Contains(laterId, orderedIds);
        Assert.True(
            Array.IndexOf(orderedIds, earlierId) < Array.IndexOf(orderedIds, laterId),
            "Discover must order start_time ascending.");

        Instant[] starts = [.. items.Select(item =>
            NodaTime.Text.InstantPattern.ExtendedIso.Parse(item.GetProperty("startTime").GetString()!).Value)];
        Assert.Equal(starts.Order().ToArray(), starts);

        // A cursor minted for "upcoming" is rejected by discover — A-077 (f)'s cross-list rule.
        string upcomingCursor = new SessionListCursor(
            _factory.Clock.GetCurrentInstant(), Guid.NewGuid(), SessionListScope.Upcoming).Encode();
        HttpResponseMessage upcomingIntoDiscover = await viewerSession.GetAsync(
            $"/api/v1/sessions?scope=discover&cursor={Uri.EscapeDataString(upcomingCursor)}");
        await SessionEndpointTestSupport.AssertProblemAsync(
            upcomingIntoDiscover, HttpStatusCode.BadRequest, "invalid_cursor");

        // And the reverse: a genuine discover cursor is rejected by "upcoming".
        if (page.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.String)
        {
            string discoverCursor = page.RootElement.GetProperty("nextCursor").GetString()!;
            HttpResponseMessage discoverIntoUpcoming = await viewerSession.GetAsync(
                $"/api/v1/sessions?scope=upcoming&cursor={Uri.EscapeDataString(discoverCursor)}");
            await SessionEndpointTestSupport.AssertProblemAsync(
                discoverIntoUpcoming, HttpStatusCode.BadRequest, "invalid_cursor");
        }
    }

    private static async Task<Guid> CreateDiscoverableAsync(
        SessionHttpSession organizerSession, string title, Instant startTime, UserId fillerId)
    {
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title,
                startTime = SessionEndpointTestSupport.Iso(startTime),
                isPrivate = false,
                minAttendance = 1,
                inviteeUserIds = new[] { fillerId.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await SessionEndpointTestSupport.SuccessJsonAsync(created))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();
    }
}
