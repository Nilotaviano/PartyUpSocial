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

    /// <summary>
    /// round-1 fix (F4): 2 sessions never mint a cursor at all (nextCursor mints only at
    /// <c>Count == pageSize == 20</c>), so the prior version's round-trip/cross-list-rejection lines
    /// were dead code guarded by an <c>if</c> that always evaluated false. 21 discoverable sessions
    /// force a real two-page split — page 1 is exactly 20 with a cursor, page 2 is the remainder with
    /// none — and every assertion below runs unconditionally, no guard that can silently skip it.
    /// </summary>
    [Fact]
    public async Task PagesByStartTimeThenIdAndRejectsAnUpcomingCursor_D149_A072()
    {
        const int SessionCount = 21;
        UserId organizerId = await _factory.SeedUserAsync(email: $"discover-page-org-{Guid.NewGuid():N}@example.test");
        UserId fillerId = await _factory.SeedUserAsync(email: $"discover-page-filler-{Guid.NewGuid():N}@example.test");
        UserId viewerId = await _factory.SeedUserAsync(email: $"discover-page-viewer-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, fillerId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, viewerId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        List<Guid> createdIds = [];
        for (int i = 0; i < SessionCount; i++)
        {
            Instant start = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1) + Duration.FromHours(i);
            createdIds.Add(await CreateDiscoverableAsync(organizerSession, $"Discover ordering — {i}", start, fillerId));
        }

        using JsonDocument firstPage = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync("/api/v1/sessions?scope=discover"));
        JsonElement[] firstItems = [.. firstPage.RootElement.GetProperty("items").EnumerateArray()];
        Assert.Equal(20, firstItems.Length);
        Assert.Equal(JsonValueKind.String, firstPage.RootElement.GetProperty("nextCursor").ValueKind);
        string discoverCursor = firstPage.RootElement.GetProperty("nextCursor").GetString()!;

        Instant[] firstStarts = [.. firstItems.Select(item =>
            NodaTime.Text.InstantPattern.ExtendedIso.Parse(item.GetProperty("startTime").GetString()!).Value)];
        Assert.Equal(firstStarts.Order().ToArray(), firstStarts);

        // The cursor round-trips on discover: page 2 carries exactly the one remaining session and no
        // further cursor.
        using JsonDocument secondPage = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync($"/api/v1/sessions?scope=discover&cursor={Uri.EscapeDataString(discoverCursor)}"));
        JsonElement[] secondItems = [.. secondPage.RootElement.GetProperty("items").EnumerateArray()];
        Assert.Single(secondItems);
        Assert.Equal(JsonValueKind.Null, secondPage.RootElement.GetProperty("nextCursor").ValueKind);

        Guid[] allReturnedIds = [.. firstItems.Concat(secondItems).Select(item => item.GetProperty("id").GetGuid())];
        Assert.Equal(createdIds.OrderBy(id => id).ToArray(), allReturnedIds.OrderBy(id => id).ToArray());

        // A cursor minted for "upcoming" is rejected by discover — A-077 (f)'s cross-list rule.
        string upcomingCursor = new SessionListCursor(
            _factory.Clock.GetCurrentInstant(), Guid.NewGuid(), SessionListScope.Upcoming).Encode();
        HttpResponseMessage upcomingIntoDiscover = await viewerSession.GetAsync(
            $"/api/v1/sessions?scope=discover&cursor={Uri.EscapeDataString(upcomingCursor)}");
        await SessionEndpointTestSupport.AssertProblemAsync(
            upcomingIntoDiscover, HttpStatusCode.BadRequest, "invalid_cursor");

        // And the reverse: a genuine discover cursor is rejected by "upcoming" — runs unconditionally
        // now that page 1 always mints a cursor.
        HttpResponseMessage discoverIntoUpcoming = await viewerSession.GetAsync(
            $"/api/v1/sessions?scope=upcoming&cursor={Uri.EscapeDataString(discoverCursor)}");
        await SessionEndpointTestSupport.AssertProblemAsync(
            discoverIntoUpcoming, HttpStatusCode.BadRequest, "invalid_cursor");
    }

    /// <summary>
    /// round-1 fix (F6): P5-VIS-01 (e)'s first half over HTTP — every (c)-matrix "returns nothing" row
    /// produces the SAME 404 problem shape (byte-compared modulo <c>traceId</c>) as a genuinely
    /// nonexistent session id on <c>GET /sessions/{id}</c>, and the two positive inherited rows (one
    /// Unconfirmed, one Confirmed) return the session instead.
    /// </summary>
    [Fact]
    public async Task NegativeInheritedOnlyCombinationsMatchTheMissingSessionShapeOverHttp_P5VIS01c()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser viewer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser filler = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewer.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, filler.Id);

        HttpResponseMessage missing = await viewerSession.GetAsync($"/api/v1/sessions/{Guid.NewGuid():D}");
        await SessionEndpointTestSupport.AssertProblemAsync(missing, HttpStatusCode.NotFound, "session_not_found");

        // (1) Private, no path at all.
        Guid privateId = await CreateDiscoverableAsync(
            organizerSession, "VIS01c private", NextStart(), filler.Id, isPrivate: true);
        await AssertSameProblemShapeAsync(await viewerSession.GetAsync($"/api/v1/sessions/{privateId:D}"), missing);

        // (2) Blocked: viewer blocks organizer.
        Guid blockedByViewerId = await CreateDiscoverableAsync(
            organizerSession, "VIS01c blocked by viewer", NextStart(), filler.Id, isPrivate: false);
        HttpResponseMessage blockedByViewer = await viewerSession.SendUnsafeAsync(
            HttpMethod.Post, "/api/v1/blocks", JsonContent.Create(new { targetUsername = organizer.Username }));
        Assert.Equal(HttpStatusCode.Created, blockedByViewer.StatusCode);
        await AssertSameProblemShapeAsync(
            await viewerSession.GetAsync($"/api/v1/sessions/{blockedByViewerId:D}"), missing);

        // (3) Blocked: organizer blocks viewer (fresh pair — the prior block already removed the
        // friendship-equivalent path between organizer and viewer, so this uses a fresh organizer).
        SessionHttpUser secondOrganizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession secondOrganizerSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, secondOrganizer.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, secondOrganizer.Id, filler.Id);
        Guid blockedByOrganizerId = await CreateDiscoverableAsync(
            secondOrganizerSession, "VIS01c blocked by organizer", NextStart(), filler.Id, isPrivate: false);
        HttpResponseMessage blockedByOrganizer = await secondOrganizerSession.SendUnsafeAsync(
            HttpMethod.Post, "/api/v1/blocks", JsonContent.Create(new { targetUsername = viewer.Username }));
        Assert.Equal(HttpStatusCode.Created, blockedByOrganizer.StatusCode);
        await AssertSameProblemShapeAsync(
            await viewerSession.GetAsync($"/api/v1/sessions/{blockedByOrganizerId:D}"), missing);

        // (4) Withdrawn join request, viewer otherwise unfriended with organizer (no other path).
        SessionHttpUser withdrawOrganizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession withdrawOrganizerSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, withdrawOrganizer.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, withdrawOrganizer.Id, filler.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, withdrawOrganizer.Id, viewer.Id);
        Guid withdrawnId = await CreateDiscoverableAsync(
            withdrawOrganizerSession, "VIS01c withdrawn request", NextStart(), filler.Id, isPrivate: false);
        Assert.Equal(
            HttpStatusCode.Created,
            (await viewerSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/sessions/{withdrawnId:D}/join-requests")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await viewerSession.SendUnsafeAsync(
                HttpMethod.Delete, $"/api/v1/sessions/{withdrawnId:D}/join-requests/mine")).StatusCode);
        // A withdrawn join request removes the requester's only path; the friendship above still grants
        // inherited access via rule 2, so this row also needs the friendship removed — approximated
        // here by blocking, which both removes the friendship and rule 4-excludes the viewer.
        Assert.Equal(
            HttpStatusCode.Created,
            (await viewerSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/blocks",
                JsonContent.Create(new { targetUsername = withdrawOrganizer.Username }))).StatusCode);
        await AssertSameProblemShapeAsync(await viewerSession.GetAsync($"/api/v1/sessions/{withdrawnId:D}"), missing);

        // (5) No-extender: a non-private session the viewer has no friend path to at all.
        SessionHttpUser strangerOrganizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession strangerOrganizerSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, strangerOrganizer.Id);
        SessionHttpUser strangerFiller = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, strangerOrganizer.Id, strangerFiller.Id);
        Guid noExtenderId = await CreateDiscoverableAsync(
            strangerOrganizerSession, "VIS01c no extender", NextStart(), strangerFiller.Id, isPrivate: false);
        await AssertSameProblemShapeAsync(await viewerSession.GetAsync($"/api/v1/sessions/{noExtenderId:D}"), missing);

        // Positive control: an inherited path through an unblocked, unattached friendship — at each of
        // Unconfirmed and Confirmed status — returns the session, not 404.
        SessionHttpUser positiveOrganizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession positiveOrganizerSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, positiveOrganizer.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, positiveOrganizer.Id, filler.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, positiveOrganizer.Id, viewer.Id);

        Guid lowThresholdId = await CreateDiscoverableAsync(
            positiveOrganizerSession, "VIS01c inherited low threshold", NextStart(), filler.Id, isPrivate: false, minAttendance: 1);
        // D-141/CreateSessionUseCase.DefaultMaxCapacity: an omitted maxCapacity defaults to 4, so a
        // minAttendance above that needs an explicit maxCapacity or D-141 rejects it as invalid, not
        // as an Unconfirmed session — this is D-141, not D-053, and orthogonal to what this row proves.
        Guid highThresholdId = await CreateDiscoverableAsync(
            positiveOrganizerSession,
            "VIS01c inherited high threshold",
            NextStart(),
            filler.Id,
            isPrivate: false,
            minAttendance: 6,
            maxCapacity: 10);

        using JsonDocument lowThresholdDetail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync($"/api/v1/sessions/{lowThresholdId:D}"));
        using JsonDocument highThresholdDetail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync($"/api/v1/sessions/{highThresholdId:D}"));

        string lowStatus = lowThresholdDetail.RootElement.GetProperty("status").GetString()!;
        string highStatus = highThresholdDetail.RootElement.GetProperty("status").GetString()!;
        Assert.NotEqual(lowStatus, highStatus);
        Assert.Contains(lowStatus, ValidStatuses);
        Assert.Contains(highStatus, ValidStatuses);
    }

    private static readonly string[] ValidStatuses = ["unconfirmed", "confirmed"];

    private static Duration _nextStartOffset = Duration.FromDays(1);

    private Instant NextStart()
    {
        Instant start = _factory.Clock.GetCurrentInstant() + _nextStartOffset;
        _nextStartOffset += Duration.FromHours(1);
        return start;
    }

    private static async Task AssertSameProblemShapeAsync(HttpResponseMessage actual, HttpResponseMessage baseline)
    {
        Assert.Equal(baseline.StatusCode, actual.StatusCode);
        using JsonDocument actualBody = JsonDocument.Parse(await actual.Content.ReadAsStringAsync());
        using JsonDocument baselineBody = JsonDocument.Parse(await baseline.Content.ReadAsStringAsync());

        // F6 names traceId as the one field that varies per request; instance echoes the request
        // path, which necessarily differs too since it embeds the target session id — comparing it
        // would compare two different URLs, not two different disclosure shapes, so it is excluded
        // for the same deliberate reason as traceId, not silently.
        string[] excluded = ["traceId", "instance"];

        string Normalize(JsonDocument document)
        {
            Dictionary<string, string> fields = document.RootElement.EnumerateObject()
                .Where(p => !excluded.Contains(p.Name))
                .ToDictionary(p => p.Name, p => p.Value.ToString());
            return string.Join(
                '',
                fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value}"));
        }

        Assert.Equal(Normalize(baselineBody), Normalize(actualBody));
    }

    private static async Task<Guid> CreateDiscoverableAsync(
        SessionHttpSession organizerSession,
        string title,
        Instant startTime,
        UserId fillerId,
        bool isPrivate = false,
        int minAttendance = 1,
        int? maxCapacity = null)
    {
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title,
                startTime = SessionEndpointTestSupport.Iso(startTime),
                isPrivate,
                minAttendance,
                maxCapacity,
                inviteeUserIds = new[] { fillerId.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await SessionEndpointTestSupport.SuccessJsonAsync(created))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();
    }
}
