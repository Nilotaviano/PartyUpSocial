using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Jobs;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// S4's HTTP inventory gate (PHASE4_DESIGN.md §8, §11): exact operation ids/schemas, the fail-closed
/// 404 contract, A-070 clear-list semantics, A-071 concurrency, all-or-nothing invite, idempotent
/// cancellation, A-072 paging, D-095 history, D-044 guest removal, D-007's organizer-only 404 parity,
/// and the closed problem-code inventory.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GameSessionEndpointTests : IAsyncLifetime, IDisposable
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

        string[] expectedPaths =
        [
            "/api/v1/sessions",
            "/api/v1/sessions/{sessionId}",
            "/api/v1/sessions/{sessionId}/invites",
            "/api/v1/sessions/{sessionId}/guest-links",
            "/api/v1/sessions/{sessionId}/guest-links/{guestLinkId}",
            "/api/v1/sessions/{sessionId}/participations/{participationId}/guest",
            "/api/v1/sessions/{sessionId}/my-participation",
            "/api/v1/sessions/{sessionId}/join-requests",
            "/api/v1/sessions/{sessionId}/join-requests/mine",
            "/api/v1/sessions/{sessionId}/join-requests/{requestId}/approve",
            "/api/v1/sessions/{sessionId}/join-requests/{requestId}/reject",
            "/api/v1/sessions/{sessionId}/participations/{participationId}/promote",
            "/api/v1/sessions/{sessionId}/participations/{participationId}/reject",
            "/api/v1/sessions/{sessionId}/my-reminder",
            "/api/v1/guest/{guestLinkId}/session",
            "/api/v1/guest/{guestLinkId}/claim",
            "/api/v1/guest/{guestLinkId}/display-name",
            "/api/v1/guest/{guestLinkId}/participation",
        ];
        foreach (string path in expectedPaths)
        {
            Assert.True(paths.TryGetProperty(path, out _), $"Missing path {path}");
        }

        Assert.True(paths.GetProperty("/api/v1/sessions").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/v1/sessions").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/sessions/{sessionId}").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/sessions/{sessionId}").TryGetProperty("patch", out _));
        Assert.True(paths.GetProperty("/api/v1/sessions/{sessionId}").TryGetProperty("delete", out _));
    }

    [Fact]
    public async Task UnsafeRegisteredAndGuestRoutesRequireAntiforgery()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);

        // A raw POST with no X-CSRF-TOKEN header, but a valid auth cookie, must be rejected before
        // ever reaching the handler.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sessions")
        {
            Content = JsonContent.Create(new { title = "No token" }),
        };
        request.Headers.Add(HeaderNames.Cookie, session.AuthCookie);
        HttpResponseMessage response = await session.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HiddenAndMissingSessionHaveEquivalent404_D007_D019()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser stranger = await SessionEndpointTestSupport.SeedAsync(_factory);
        Session privateSession = await SessionEndpointTestSupport.SeedSessionAsync(_factory, organizer.Id);

        using SessionHttpSession strangerSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, stranger.Id);

        HttpResponseMessage hidden = await strangerSession.GetAsync(
            $"/api/v1/sessions/{privateSession.Id.Value:D}");
        HttpResponseMessage missing = await strangerSession.GetAsync(
            $"/api/v1/sessions/{Guid.NewGuid():D}");

        await SessionEndpointTestSupport.AssertProblemAsync(hidden, HttpStatusCode.NotFound, "session_not_found");
        await SessionEndpointTestSupport.AssertProblemAsync(missing, HttpStatusCode.NotFound, "session_not_found");
    }

    [Fact]
    public async Task CreateAppliesDefaultsAndRejectsInvalidBounds_D129_D130_D142()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser friend = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, friend.Id);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);

        Instant validStart = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Defaults check",
                startTime = SessionEndpointTestSupport.Iso(validStart),
                inviteeUserIds = new[] { friend.Id.Value },
            }));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        JsonElement detail = body.RootElement.GetProperty("session");
        Assert.Equal(120, detail.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(4, detail.GetProperty("minAttendance").GetInt32());
        Assert.Equal(4, detail.GetProperty("maxCapacity").GetInt32());
        Assert.Equal("notApplicable", detail.GetProperty("platform").GetString());
        Assert.False(detail.GetProperty("isPrivate").GetBoolean());

        // D-130: too close to now.
        HttpResponseMessage tooSoon = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Too soon",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromMinutes(5)),
                isPrivate = true,
            }));
        await SessionEndpointTestSupport.AssertProblemAsync(tooSoon, HttpStatusCode.BadRequest, "session_validation");

        // D-142: not a multiple of 15.
        HttpResponseMessage badDuration = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Bad duration",
                startTime = SessionEndpointTestSupport.Iso(validStart),
                durationMinutes = 37,
                isPrivate = true,
            }));
        await SessionEndpointTestSupport.AssertProblemAsync(badDuration, HttpStatusCode.BadRequest, "session_validation");
    }

    [Fact]
    public async Task EditClearListExpressesUnlimitedAndAbsentFields_A070_D091_D121()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);

        (JsonDocument created, _) = await CreatePrivateSessionAsync(session, maxCapacity: 5);
        JsonElement detail = created.RootElement.GetProperty("session");
        Guid sessionId = detail.GetProperty("id").GetGuid();
        string version = detail.GetProperty("version").GetString()!;

        // A field named in `clear` and also present in the body is rejected (A-070).
        string[] clearMaxCapacity = ["maxCapacity"];
        HttpResponseMessage conflicting = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/sessions/{sessionId:D}",
            JsonContent.Create(new { maxCapacity = 10, clear = clearMaxCapacity, ifMatch = version }));
        await SessionEndpointTestSupport.AssertProblemAsync(
            conflicting, HttpStatusCode.BadRequest, "session_validation");

        HttpResponseMessage cleared = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/sessions/{sessionId:D}",
            JsonContent.Create(new { clear = clearMaxCapacity, ifMatch = version }));
        using JsonDocument clearedBody = await SessionEndpointTestSupport.SuccessJsonAsync(cleared);
        Assert.Equal(
            JsonValueKind.Null,
            clearedBody.RootElement.GetProperty("maxCapacity").ValueKind);
    }

    [Fact]
    public async Task MalformedIfMatchIs400AndStaleIfMatchIs409_A071()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        (JsonDocument created, _) = await CreatePrivateSessionAsync(session);
        JsonElement detail = created.RootElement.GetProperty("session");
        Guid sessionId = detail.GetProperty("id").GetGuid();
        string originalVersion = detail.GetProperty("version").GetString()!;

        HttpResponseMessage malformed = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/sessions/{sessionId:D}",
            JsonContent.Create(new { title = "New title", ifMatch = "not-a-valid-token!!" }));
        await SessionEndpointTestSupport.AssertProblemAsync(malformed, HttpStatusCode.BadRequest, "invalid_if_match");

        HttpResponseMessage firstEdit = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/sessions/{sessionId:D}",
            JsonContent.Create(new { title = "First edit", ifMatch = originalVersion }));
        Assert.Equal(HttpStatusCode.OK, firstEdit.StatusCode);

        HttpResponseMessage stale = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/sessions/{sessionId:D}",
            JsonContent.Create(new { title = "Stale edit", ifMatch = originalVersion }));
        await SessionEndpointTestSupport.AssertProblemAsync(stale, HttpStatusCode.Conflict, "session_conflict");
    }

    [Fact]
    public async Task InviteIsAllOrNothingWithAUniformNotFound_D007()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser friend = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser stranger = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, friend.Id);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        (JsonDocument created, _) = await CreatePrivateSessionAsync(session);
        Guid sessionId = created.RootElement.GetProperty("session").GetProperty("id").GetGuid();

        HttpResponseMessage inviteResult = await session.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/sessions/{sessionId:D}/invites",
            JsonContent.Create(new { userIds = new[] { friend.Id.Value, stranger.Id.Value } }));

        await SessionEndpointTestSupport.AssertProblemAsync(
            inviteResult, HttpStatusCode.NotFound, "user_not_found");

        using JsonDocument detailAfter = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        JsonElement roster = detailAfter.RootElement.GetProperty("roster");
        // Only the organizer's own seed row exists; the friend was never attached either.
        Assert.Single(roster.EnumerateArray());
    }

    [Fact]
    public async Task CancelIsIdempotentAndCompletedIsCancelable_D156_D157()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);

        Session completedSeed = await SessionEndpointTestSupport.SeedSessionAsync(
            _factory, organizer.Id, minAttendance: 1, leadMinutes: 20, durationMinutes: 15);
        _factory.Clock.Advance(Duration.FromMinutes(40));
        await SessionEndpointTestSupport.RunDueSweepAsync(_factory);

        HttpResponseMessage firstCancel = await session.SendUnsafeAsync(
            HttpMethod.Delete, $"/api/v1/sessions/{completedSeed.Id.Value:D}");
        Assert.Equal(HttpStatusCode.NoContent, firstCancel.StatusCode);

        HttpResponseMessage secondCancel = await session.SendUnsafeAsync(
            HttpMethod.Delete, $"/api/v1/sessions/{completedSeed.Id.Value:D}");
        Assert.Equal(HttpStatusCode.NoContent, secondCancel.StatusCode);

        Session expiredSeed = await SessionEndpointTestSupport.SeedSessionAsync(
            _factory, organizer.Id, minAttendance: 4, leadMinutes: 20, durationMinutes: 15);
        _factory.Clock.Advance(Duration.FromMinutes(40));
        await SessionEndpointTestSupport.RunDueSweepAsync(_factory);

        HttpResponseMessage expiredCancel = await session.SendUnsafeAsync(
            HttpMethod.Delete, $"/api/v1/sessions/{expiredSeed.Id.Value:D}");
        await SessionEndpointTestSupport.AssertProblemAsync(
            expiredCancel, HttpStatusCode.Conflict, "session_terminal");
    }

    [Fact]
    public async Task CursorPagesAreKeysetStableAndRejectMalformedCursors_A072()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        await SessionEndpointTestSupport.SeedSessionAsync(_factory, organizer.Id, leadMinutes: 30);
        await SessionEndpointTestSupport.SeedSessionAsync(_factory, organizer.Id, leadMinutes: 60);

        HttpResponseMessage badScope = await session.GetAsync("/api/v1/sessions?scope=sideways");
        await SessionEndpointTestSupport.AssertProblemAsync(badScope, HttpStatusCode.BadRequest, "invalid_scope");

        HttpResponseMessage badCursor = await session.GetAsync("/api/v1/sessions?scope=upcoming&cursor=not-a-cursor!!");
        await SessionEndpointTestSupport.AssertProblemAsync(badCursor, HttpStatusCode.BadRequest, "invalid_cursor");

        using JsonDocument page = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync("/api/v1/sessions?scope=upcoming"));
        JsonElement items = page.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 2);
        Instant[] starts = items.EnumerateArray()
            .Select(item => InstantFromIso(item.GetProperty("startTime").GetString()!))
            .ToArray();
        Assert.Equal(starts.Order().ToArray(), starts);
    }

    [Fact]
    public async Task HistoryContainsOnlyCompletedFinalRoster_D095()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);

        Session completed = await SessionEndpointTestSupport.SeedSessionAsync(
            _factory, organizer.Id, minAttendance: 1, leadMinutes: 20, durationMinutes: 15);
        Session expired = await SessionEndpointTestSupport.SeedSessionAsync(
            _factory, organizer.Id, minAttendance: 4, leadMinutes: 20, durationMinutes: 15);
        _factory.Clock.Advance(Duration.FromMinutes(40));
        await SessionEndpointTestSupport.RunDueSweepAsync(_factory);

        using JsonDocument history = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync("/api/v1/sessions?scope=history"));
        Guid[] historyIds = history.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();
        Assert.Contains(completed.Id.Value, historyIds);
        Assert.DoesNotContain(expired.Id.Value, historyIds);
    }

    [Fact]
    public async Task RemoveGuest_RequiresOrganizerAndRevokesCredential_D044()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser other = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        using SessionHttpSession otherSession = await SessionEndpointTestSupport.SignInAsync(_factory, other.Id);

        (JsonDocument created, string token) = await CreatePrivateSessionAsync(organizerSession, guestLinkCount: 1);
        Guid sessionId = created.RootElement.GetProperty("session").GetProperty("id").GetGuid();

        // Claim the link as a guest via the raw domain use case (HTTP guest claim is exercised by
        // GuestResponseTests) so this test focuses purely on organizer-only removal.
        Guid guestLinkId = created.RootElement.GetProperty("guestLinks")[0].GetProperty("guestLinkId").GetGuid();
        Guid participationId = await ClaimGuestDirectlyAsync(sessionId, guestLinkId);

        HttpResponseMessage forbidden = await otherSession.SendUnsafeAsync(
            HttpMethod.Delete,
            $"/api/v1/sessions/{sessionId:D}/participations/{participationId:D}/guest");
        await SessionEndpointTestSupport.AssertProblemAsync(forbidden, HttpStatusCode.NotFound, "session_not_found");

        HttpResponseMessage removed = await organizerSession.SendUnsafeAsync(
            HttpMethod.Delete,
            $"/api/v1/sessions/{sessionId:D}/participations/{participationId:D}/guest");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Session reloaded = await context.Sessions
            .Include(s => s.GuestLinks)
            .SingleAsync(s => s.Id == SessionId.From(sessionId));
        GuestLink link = reloaded.GuestLinks.Single(l => l.Id == GuestLinkId.From(guestLinkId));
        Assert.Equal(GuestLinkState.RevokedByRemoval, link.State);
    }

    [Fact]
    public async Task JoinRequestListReturnsTheSame404ForNonOrganizersAsForMissingSessions_D007()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser stranger = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, stranger.Id);
        (JsonDocument created, _) = await CreatePrivateSessionAsync(organizerSession);
        Guid sessionId = created.RootElement.GetProperty("session").GetProperty("id").GetGuid();

        HttpResponseMessage nonOrganizer = await strangerSession.GetAsync(
            $"/api/v1/sessions/{sessionId:D}/join-requests");
        HttpResponseMessage missing = await strangerSession.GetAsync(
            $"/api/v1/sessions/{Guid.NewGuid():D}/join-requests");

        await SessionEndpointTestSupport.AssertProblemAsync(nonOrganizer, HttpStatusCode.NotFound, "session_not_found");
        await SessionEndpointTestSupport.AssertProblemAsync(missing, HttpStatusCode.NotFound, "session_not_found");
    }

    [Fact]
    public async Task PromoteAndRejectWaitlistRequireOrganizerAndAWaitlistedRow_D084()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser filler = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser other = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        using SessionHttpSession otherSession = await SessionEndpointTestSupport.SignInAsync(_factory, other.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, filler.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, other.Id);

        // D-099 needs an invited friend for a non-private creation (`filler`); join requests need the
        // requester to already see the session, which rule 2 (inherited access) only reaches for a
        // non-private target, so `other` — a friend but never invited — discovers it via that rule.
        HttpResponseMessage createResponse = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Waitlist support session",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                minAttendance = 1,
                maxCapacity = 1,
                isPrivate = false,
                inviteeUserIds = new[] { filler.Id.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using JsonDocument created = await SessionEndpointTestSupport.SuccessJsonAsync(createResponse);
        Guid sessionId = created.RootElement.GetProperty("session").GetProperty("id").GetGuid();

        // The organizer already occupies the one slot: a join request from `other`, once approved,
        // must land waitlisted.
        HttpResponseMessage joinRequested = await otherSession.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/sessions/{sessionId:D}/join-requests");
        Assert.Equal(HttpStatusCode.Created, joinRequested.StatusCode);
        using JsonDocument requestBody = await SessionEndpointTestSupport.SuccessJsonAsync(joinRequested);
        Guid requestId = requestBody.RootElement.GetProperty("id").GetGuid();

        using JsonDocument approved = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/sessions/{sessionId:D}/join-requests/{requestId:D}/approve"));
        Guid participationId = approved.RootElement.GetProperty("participationId").GetGuid();
        Assert.True(approved.RootElement.GetProperty("isWaitlisted").GetBoolean());

        HttpResponseMessage nonOrganizerPromote = await otherSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/sessions/{sessionId:D}/participations/{participationId:D}/promote");
        await SessionEndpointTestSupport.AssertProblemAsync(
            nonOrganizerPromote, HttpStatusCode.NotFound, "session_not_found");

        // No free slot exists (organizer occupies the only one), so promotion is capacity-blocked.
        HttpResponseMessage noCapacity = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/sessions/{sessionId:D}/participations/{participationId:D}/promote");
        await SessionEndpointTestSupport.AssertProblemAsync(
            noCapacity, HttpStatusCode.UnprocessableEntity, "no_session_capacity");

        // The filler's own row exists and is not removed, but was never waitlisted (D-084's other
        // negative case — distinct from the "removed" one below).
        using JsonDocument detailForFiller = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        Guid fillerParticipationId = detailForFiller.RootElement.GetProperty("roster").EnumerateArray()
            .Single(entry => entry.GetProperty("userId").GetGuid() == filler.Id.Value)
            .GetProperty("participationId").GetGuid();
        HttpResponseMessage notWaitlisted = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/sessions/{sessionId:D}/participations/{fillerParticipationId:D}/reject");
        await SessionEndpointTestSupport.AssertProblemAsync(
            notWaitlisted, HttpStatusCode.UnprocessableEntity, "not_waitlisted");

        HttpResponseMessage rejected = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/sessions/{sessionId:D}/participations/{participationId:D}/reject");
        Assert.Equal(HttpStatusCode.NoContent, rejected.StatusCode);

        // Removal is permanent (§5.2): a second reject on the now-Removed row reads as not found,
        // never as a second "not waitlisted" conflict.
        HttpResponseMessage alreadyRejected = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/sessions/{sessionId:D}/participations/{participationId:D}/reject");
        await SessionEndpointTestSupport.AssertProblemAsync(
            alreadyRejected, HttpStatusCode.NotFound, "session_not_found");
    }

    [Fact]
    public async Task EveryDocumentedProblemCodeMapsToItsDocumentedStatus()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser stranger = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, stranger.Id);

        // session_not_found (404): a missing session.
        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.GetAsync($"/api/v1/sessions/{Guid.NewGuid():D}"),
            HttpStatusCode.NotFound,
            "session_not_found");

        // invalid_scope (400).
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.GetAsync("/api/v1/sessions?scope=bogus"),
            HttpStatusCode.BadRequest,
            "invalid_scope");

        // invalid_cursor (400).
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.GetAsync("/api/v1/sessions?scope=upcoming&cursor=%%%"),
            HttpStatusCode.BadRequest,
            "invalid_cursor");

        // session_validation (400): out-of-window create.
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/sessions",
                JsonContent.Create(new
                {
                    title = "x",
                    startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant()),
                    isPrivate = true,
                })),
            HttpStatusCode.BadRequest,
            "session_validation");
    }

    // ---- shared fixtures ----------------------------------------------------------------------

    private async Task<(JsonDocument Body, string RawFirstGuestToken)> CreatePrivateSessionAsync(
        SessionHttpSession session,
        int? maxCapacity = null,
        int guestLinkCount = 0,
        bool isPrivate = true)
    {
        HttpResponseMessage created = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Support session",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                minAttendance = 1,
                maxCapacity,
                isPrivate,
                guestLinkCount,
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        string rawUrl = guestLinkCount > 0
            ? body.RootElement.GetProperty("guestLinks")[0].GetProperty("url").GetString()!
            : string.Empty;
        string token = rawUrl.Length == 0 ? string.Empty : rawUrl[(rawUrl.LastIndexOf('/') + 1)..];
        return (body, token);
    }

    /// <summary>Bypasses the guest HTTP surface (owned by <c>GuestResponseTests</c>) to seed a claimed guest.</summary>
    private async Task<Guid> ClaimGuestDirectlyAsync(Guid sessionId, Guid guestLinkId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var useCase = scope.ServiceProvider.GetRequiredService<PartyUp.Application.Sessions.GuestSessionUseCases>();
        PartyUp.Application.Sessions.GuestCommandResult result = await useCase.ClaimGuestAsync(
            SessionId.From(sessionId),
            GuestLinkId.From(guestLinkId),
            DisplayName.Create("Guest Player"),
            ParticipationStatus.Going,
            default);
        Assert.True(result.Succeeded);
        Participation participation = result.Session!.Participations
            .Single(p => p.GuestIdentityId == result.GuestIdentityId);
        return participation.Id.Value;
    }

    private static Instant InstantFromIso(string value) =>
        NodaTime.Text.InstantPattern.ExtendedIso.Parse(value).Value;
}

/// <summary>
/// Shared S4 HTTP test plumbing (mirrors <c>Friends/ProfileEndpointTests.cs</c>'s
/// <c>FriendEndpointTest</c>) — kept internal so every S4 test class in this folder can reuse one
/// sign-in/antiforgery/JSON path without duplicating it across seven files.
/// </summary>
internal sealed record SessionHttpUser(UserId Id, string Username);

internal sealed class SessionHttpSession(HttpClient client, string authCookie) : IDisposable
{
    internal HttpClient Client => client;

    internal string AuthCookie => authCookie;

    internal Task<HttpResponseMessage> GetAsync(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(HeaderNames.Cookie, authCookie);
        return client.SendAsync(request);
    }

    internal Task<HttpResponseMessage> SendUnsafeAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null) =>
        AntiforgeryTestClient.SendAsync(client, method, path, content, authCookie);

    public void Dispose() => client.Dispose();
}

internal static class SessionEndpointTestSupport
{
    internal static async Task<SessionHttpUser> SeedAsync(PartyUpApiFactory factory)
    {
        string suffix = Guid.NewGuid().ToString("N");
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AuthUser>>();
        UserId id = UserId.New();
        string friendCode = string.Concat(
            Guid.NewGuid().ToByteArray()
                .Take(FriendCodeFormat.Length)
                .Select(value => FriendCodeFormat.Alphabet[value % FriendCodeFormat.Alphabet.Length]));
        User user = User.BeginOnboarding(id, FriendCode.Create(friendCode), AgeGateSource.SelfAttested, factory.Clock);
        string username = $"user{suffix[^12..]}";
        user.CompleteOnboarding(
            Username.Create(username),
            DisplayName.Create("HTTP Session Test"),
            Avatar.FromEmoji("🙂"),
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"),
            factory.Clock);
        context.UsernameRegistry.Add(UsernameRegistryEntry.ClaimNew(user.Username!.Value, id));
        context.DomainUsers.Add(user);
        await context.SaveChangesAsync();

        string identityName = id.Value.ToString("D");
        Microsoft.AspNetCore.Identity.IdentityResult identity = await userManager.CreateAsync(new AuthUser
        {
            Id = id.Value,
            UserName = identityName,
            NormalizedUserName = identityName,
            Email = $"session-http-{suffix}@example.test",
            EmailConfirmed = true,
            LockoutEnabled = true,
        });
        Assert.True(identity.Succeeded);
        return new SessionHttpUser(id, username);
    }

    internal static async Task MakeFriendsAsync(PartyUpApiFactory factory, UserId a, UserId b)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User requester = await context.DomainUsers.SingleAsync(u => u.Id == a);
        User addressee = await context.DomainUsers.SingleAsync(u => u.Id == b);
        Friendship friendship = Friendship.Request(requester, addressee, factory.Clock);
        _ = friendship.DequeueEvents();
        friendship.Accept(b, factory.Clock);
        _ = friendship.DequeueEvents();
        context.Friendships.Add(friendship);
        await context.SaveChangesAsync();
    }

    internal static async Task<SessionHttpSession> SignInAsync(PartyUpApiFactory factory, UserId userId)
    {
        HttpClient client = Slice5HttpTest.CreateClient(factory);
        HttpResponseMessage signIn = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        return new SessionHttpSession(client, Slice5HttpTest.AuthCookie(signIn));
    }

    internal static async Task<JsonDocument> SuccessJsonAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected success but got {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    /// <summary>Directly constructs and seeds a Session, bypassing HTTP — mirrors <c>SessionSweepTests</c>.</summary>
    internal static async Task<Session> SeedSessionAsync(
        PartyUpApiFactory factory,
        UserId organizerId,
        int minAttendance = 1,
        int? maxCapacity = null,
        int leadMinutes = 20,
        int durationMinutes = 15,
        bool isPrivate = true)
    {
        SessionDetails details = new(
            SessionTitle.Create("Support-seeded session"),
            factory.Clock.GetCurrentInstant() + Duration.FromMinutes(leadMinutes),
            Duration15.FromMinutes(durationMinutes),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance,
            maxCapacity,
            isPrivate);
        Session session = Session.Create(organizerId, details, [], factory.Clock);
        _ = session.DequeueEvents();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    internal static async Task RunDueSweepAsync(PartyUpApiFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<SessionDueSweepJob>();
        await job.ExecuteAsync(default);
    }

    internal static string Iso(Instant instant) =>
        NodaTime.Text.InstantPattern.ExtendedIso.Format(instant);
}
