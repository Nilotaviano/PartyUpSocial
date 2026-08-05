using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api;
using PartyUp.Api.Hosting;
using PartyUp.Application.Sessions;
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
    private static readonly string[] ExpectedClearFieldEnumValues = ["game", "maxCapacity", "notes"];
    private static readonly string[] ClearNotesOnly = ["notes"];

    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// PHASE4_DESIGN.md §8/§8.1 tables, verbatim: every row's method, path, and operation id. This
    /// asserts the CLOSED set — an extra path, an extra method on a documented path, or a drifted
    /// operation id all fail the test, not just a missing one.
    /// </summary>
    [Fact]
    public async Task InventoryHasExactOperationIdsAndSchemas()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement paths = document.RootElement.GetProperty("paths");

        // path -> (HTTP method -> operation id), §8/§8.1 verbatim.
        Dictionary<string, Dictionary<string, string>> expected = new()
        {
            ["/api/v1/sessions"] = new()
            {
                ["post"] = ApiOperationIds.CreateSession,
                ["get"] = ApiOperationIds.ListSessions,
            },
            ["/api/v1/sessions/{sessionId}"] = new()
            {
                ["get"] = ApiOperationIds.GetSessionDetail,
                ["patch"] = ApiOperationIds.EditSession,
                ["delete"] = ApiOperationIds.CancelSession,
            },
            ["/api/v1/sessions/{sessionId}/invites"] = new()
            {
                ["post"] = ApiOperationIds.InviteSessionUsers,
            },
            ["/api/v1/sessions/{sessionId}/guest-links"] = new()
            {
                ["post"] = ApiOperationIds.CreateSessionGuestLinks,
            },
            ["/api/v1/sessions/{sessionId}/guest-links/{guestLinkId}"] = new()
            {
                ["delete"] = ApiOperationIds.RevokeSessionGuestLink,
            },
            ["/api/v1/sessions/{sessionId}/participations/{participationId}/guest"] = new()
            {
                ["delete"] = ApiOperationIds.RemoveSessionGuest,
            },
            ["/api/v1/sessions/{sessionId}/my-participation"] = new()
            {
                ["put"] = ApiOperationIds.RespondToSessionInvitation,
                ["delete"] = ApiOperationIds.LeaveSession,
            },
            ["/api/v1/sessions/{sessionId}/join-requests"] = new()
            {
                ["post"] = ApiOperationIds.RequestSessionJoin,
                ["get"] = ApiOperationIds.ListSessionJoinRequests,
            },
            ["/api/v1/sessions/{sessionId}/join-requests/mine"] = new()
            {
                ["delete"] = ApiOperationIds.WithdrawSessionJoinRequest,
            },
            ["/api/v1/sessions/{sessionId}/join-requests/{requestId}/approve"] = new()
            {
                ["post"] = ApiOperationIds.ApproveSessionJoinRequest,
            },
            ["/api/v1/sessions/{sessionId}/join-requests/{requestId}/reject"] = new()
            {
                ["post"] = ApiOperationIds.RejectSessionJoinRequest,
            },
            ["/api/v1/sessions/{sessionId}/participations/{participationId}/promote"] = new()
            {
                ["post"] = ApiOperationIds.PromoteSessionWaitlistParticipant,
            },
            ["/api/v1/sessions/{sessionId}/participations/{participationId}/reject"] = new()
            {
                ["post"] = ApiOperationIds.RejectSessionWaitlistParticipant,
            },
            ["/api/v1/sessions/{sessionId}/my-reminder"] = new()
            {
                ["put"] = ApiOperationIds.SetSessionReminder,
            },
            ["/api/v1/guest/{guestLinkId}/session"] = new()
            {
                ["get"] = ApiOperationIds.GetGuestSession,
            },
            ["/api/v1/guest/{guestLinkId}/claim"] = new()
            {
                ["post"] = ApiOperationIds.ClaimGuestSessionLink,
            },
            ["/api/v1/guest/{guestLinkId}/display-name"] = new()
            {
                ["put"] = ApiOperationIds.RenameGuestSessionIdentity,
            },
            ["/api/v1/guest/{guestLinkId}/participation"] = new()
            {
                ["put"] = ApiOperationIds.RespondAsGuest,
            },
        };

        // Exact path set: no extras, nothing missing (this is the S4 session/guest surface only —
        // Slice 1-3 routes live under a distinct prefix and are not part of this inventory).
        string[] sessionScopedActualPaths = paths.EnumerateObject()
            .Select(p => p.Name)
            .Where(name => name.StartsWith("/api/v1/sessions", StringComparison.Ordinal)
                || name.StartsWith("/api/v1/guest/", StringComparison.Ordinal))
            .Order()
            .ToArray();
        Assert.Equal(expected.Keys.Order().ToArray(), sessionScopedActualPaths);

        foreach ((string path, Dictionary<string, string> methods) in expected)
        {
            JsonElement pathItem = paths.GetProperty(path);

            // Exact method set per path: only HTTP-verb keys count (a shared "parameters" sibling,
            // if present, is not a method and must not inflate the set).
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

    /// <summary>
    /// F8 / A-070 / §8: the clear-list vocabulary is a closed camelCase enum on the wire and in the
    /// OpenAPI document — never the PascalCase C# member names.
    /// </summary>
    [Fact]
    public async Task ClearFieldEnumIsCamelCaseOnWireAndInOpenApiDocument()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        JsonElement clearProperty = schemas.GetProperty("EditSessionRequest")
            .GetProperty("properties").GetProperty("clear");
        JsonElement items = clearProperty.GetProperty("items");
        JsonElement enumSchema = items.TryGetProperty("$ref", out JsonElement refValue)
            ? schemas.GetProperty(refValue.GetString()!.Split('/')[^1])
            : items;
        string[] documentedValues = enumSchema.GetProperty("enum")
            .EnumerateArray()
            .Select(v => v.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedClearFieldEnumValues, documentedValues);

        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        Session session = await SessionEndpointTestSupport.SeedSessionAsync(_factory, organizer.Id);
        using SessionHttpSession organizerSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);

        HttpResponseMessage getDetail = await organizerSession.GetAsync(
            $"/api/v1/sessions/{session.Id.Value:D}");
        using JsonDocument detailBody = await SessionEndpointTestSupport.SuccessJsonAsync(getDetail);
        string ifMatch = detailBody.RootElement.GetProperty("version").GetString()!;

        HttpResponseMessage edit = await organizerSession.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/sessions/{session.Id.Value:D}",
            JsonContent.Create(new { clear = ClearNotesOnly, ifMatch }));
        using JsonDocument editedBody = await SessionEndpointTestSupport.SuccessJsonAsync(edit);
        Assert.Equal(string.Empty, editedBody.RootElement.GetProperty("notes").GetString());
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
    public async Task CreateAppliesDefaultsAndRejectsInvalidBounds_D129_D130_D141_D142()
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

        // D-141: maxCapacity below minAttendance.
        HttpResponseMessage badCapacity = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Bad capacity bound",
                startTime = SessionEndpointTestSupport.Iso(validStart),
                minAttendance = 3,
                maxCapacity = 2,
                isPrivate = true,
            }));
        await SessionEndpointTestSupport.AssertProblemAsync(badCapacity, HttpStatusCode.BadRequest, "session_validation");

        // D-141: minAttendance below 1.
        HttpResponseMessage badMinimum = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Bad minimum bound",
                startTime = SessionEndpointTestSupport.Iso(validStart),
                minAttendance = 0,
                isPrivate = true,
            }));
        await SessionEndpointTestSupport.AssertProblemAsync(badMinimum, HttpStatusCode.BadRequest, "session_validation");
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

        // A well-formed-but-too-short base64url string decodes fine but is nowhere near the shape of a
        // real Data Protection ciphertext.
        HttpResponseMessage tooShort = await session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/sessions/{sessionId:D}",
            JsonContent.Create(new { title = "New title", ifMatch = "AA" }));
        await SessionEndpointTestSupport.AssertProblemAsync(tooShort, HttpStatusCode.BadRequest, "invalid_if_match");

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

    /// <summary>
    /// A-071/PHASE4_DESIGN.md §4.2 step 2-3: the comparison point is the freshly locked row's own
    /// <c>xmin</c>, not an earlier, unlocked read. Two PATCHes racing on the same initially-valid
    /// <c>ifMatch</c> genuinely contend on the session's <c>FOR NO KEY UPDATE</c> parent lock inside
    /// <c>EditSessionUseCase</c> — the loser only unblocks after the winner's transaction has committed
    /// a new <c>xmin</c>, so its in-transaction comparison (not a stale pre-read) is what catches it.
    /// </summary>
    [Fact]
    public async Task ConcurrentEditsWithTheSameInitialVersion_ExactlyOneWinsUnderLock_A071()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        (JsonDocument created, _) = await CreatePrivateSessionAsync(session);
        JsonElement detail = created.RootElement.GetProperty("session");
        Guid sessionId = detail.GetProperty("id").GetGuid();
        string version = detail.GetProperty("version").GetString()!;

        Task<HttpResponseMessage> first = session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/sessions/{sessionId:D}",
            JsonContent.Create(new { title = "Racing edit A", ifMatch = version }));
        Task<HttpResponseMessage> second = session.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/sessions/{sessionId:D}",
            JsonContent.Create(new { title = "Racing edit B", ifMatch = version }));

        HttpResponseMessage[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        HttpResponseMessage conflict = results.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        await SessionEndpointTestSupport.AssertProblemAsync(conflict, HttpStatusCode.Conflict, "session_conflict");
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
    public async Task CursorsAreBoundToTheirIssuingListAndRejectedAcrossLists_A072()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        (JsonDocument created, _) = await CreatePrivateSessionAsync(session);
        Guid sessionId = created.RootElement.GetProperty("session").GetProperty("id").GetGuid();

        // A cursor genuinely issued for the "upcoming" session list must never be honored by the
        // "history" list, even though both are decoded by the same shared TryDecodeCursor path.
        string upcomingCursor = new SessionListCursor(
            _factory.Clock.GetCurrentInstant(), Guid.NewGuid(), SessionListScope.Upcoming).Encode();
        HttpResponseMessage upcomingIntoHistory = await session.GetAsync(
            $"/api/v1/sessions?scope=history&cursor={Uri.EscapeDataString(upcomingCursor)}");
        await SessionEndpointTestSupport.AssertProblemAsync(
            upcomingIntoHistory, HttpStatusCode.BadRequest, "invalid_cursor");

        // A cursor issued for a session list (upcoming/history) must never be honored by the
        // join-request list either.
        string sessionListCursor = new SessionListCursor(
            _factory.Clock.GetCurrentInstant(), Guid.NewGuid(), SessionListScope.History).Encode();
        HttpResponseMessage sessionListIntoJoinRequests = await session.GetAsync(
            $"/api/v1/sessions/{sessionId:D}/join-requests?cursor={Uri.EscapeDataString(sessionListCursor)}");
        await SessionEndpointTestSupport.AssertProblemAsync(
            sessionListIntoJoinRequests, HttpStatusCode.BadRequest, "invalid_cursor");

        // Sanity: a cursor genuinely scoped to its own list decodes and is accepted (no items match,
        // but the request is not rejected as malformed).
        string joinRequestsCursor = new SessionListCursor(
            _factory.Clock.GetCurrentInstant(), Guid.NewGuid(), SessionListScope.JoinRequests).Encode();
        HttpResponseMessage ownScope = await session.GetAsync(
            $"/api/v1/sessions/{sessionId:D}/join-requests?cursor={Uri.EscapeDataString(joinRequestsCursor)}");
        Assert.Equal(HttpStatusCode.OK, ownScope.StatusCode);
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

        // session_time_locked (422): the start time cannot be edited after the session starts (D-168).
        Instant timeLockedStart = _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(20);
        HttpResponseMessage timeLockedCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Time-locked edit",
                startTime = SessionEndpointTestSupport.Iso(timeLockedStart),
                minAttendance = 1,
                isPrivate = true,
            }));
        Assert.Equal(HttpStatusCode.Created, timeLockedCreated.StatusCode);
        using JsonDocument timeLockedBody = await SessionEndpointTestSupport.SuccessJsonAsync(timeLockedCreated);
        JsonElement timeLockedDetail = timeLockedBody.RootElement.GetProperty("session");
        Guid timeLockedSessionId = timeLockedDetail.GetProperty("id").GetGuid();
        string timeLockedVersion = timeLockedDetail.GetProperty("version").GetString()!;

        _factory.Clock.Advance(Duration.FromMinutes(25));

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Patch,
                $"/api/v1/sessions/{timeLockedSessionId:D}",
                JsonContent.Create(new
                {
                    startTime = SessionEndpointTestSupport.Iso(timeLockedStart + Duration.FromMinutes(30)),
                    ifMatch = timeLockedVersion,
                })),
            HttpStatusCode.UnprocessableEntity,
            "session_time_locked");

        // capacity_below_occupancy (422): shrinking maxCapacity below active occupancy (D-121).
        SessionHttpUser capacityFriend = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, capacityFriend.Id);
        using SessionHttpSession capacityFriendSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, capacityFriend.Id);

        HttpResponseMessage capacityCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Capacity below occupancy",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                minAttendance = 1,
                maxCapacity = 2,
                isPrivate = true,
                inviteeUserIds = new[] { capacityFriend.Id.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, capacityCreated.StatusCode);
        Guid capacitySessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(capacityCreated))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.OK,
            (await capacityFriendSession.SendUnsafeAsync(
                HttpMethod.Put,
                $"/api/v1/sessions/{capacitySessionId:D}/my-participation",
                JsonContent.Create(new { response = "Going" }))).StatusCode);

        using JsonDocument capacityDetailAfterRsvp = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{capacitySessionId:D}"));
        string capacityVersion = capacityDetailAfterRsvp.RootElement.GetProperty("version").GetString()!;

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Patch,
                $"/api/v1/sessions/{capacitySessionId:D}",
                JsonContent.Create(new { maxCapacity = 1, ifMatch = capacityVersion })),
            HttpStatusCode.UnprocessableEntity,
            "capacity_below_occupancy");

        // waitlist_requires_capacity (422): clearing maxCapacity while a waitlisted row still exists (D-091, §12-1).
        SessionHttpUser waitlistFriend = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser waitlistFiller = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, waitlistFriend.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, waitlistFiller.Id);
        using SessionHttpSession waitlistFriendSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, waitlistFriend.Id);

        HttpResponseMessage waitlistCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Waitlist requires capacity",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                minAttendance = 1,
                maxCapacity = 1,
                isPrivate = false,
                // D-099: a non-private session needs at least one directly invited friend at creation;
                // this filler stays Pending and never consumes a slot, so it does not affect the
                // maxCapacity=1/waitlist mechanics under test.
                inviteeUserIds = new[] { waitlistFiller.Id.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, waitlistCreated.StatusCode);
        Guid waitlistSessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(waitlistCreated))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        HttpResponseMessage waitlistJoinRequested = await waitlistFriendSession.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/sessions/{waitlistSessionId:D}/join-requests");
        Assert.Equal(HttpStatusCode.Created, waitlistJoinRequested.StatusCode);
        Guid waitlistRequestId = (await SessionEndpointTestSupport.SuccessJsonAsync(waitlistJoinRequested))
            .RootElement.GetProperty("id").GetGuid();

        using JsonDocument waitlistApproved = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/sessions/{waitlistSessionId:D}/join-requests/{waitlistRequestId:D}/approve"));
        Assert.True(waitlistApproved.RootElement.GetProperty("isWaitlisted").GetBoolean());

        using JsonDocument waitlistSessionDetail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{waitlistSessionId:D}"));
        string waitlistVersion = waitlistSessionDetail.RootElement.GetProperty("version").GetString()!;

        string[] waitlistClearMaxCapacity = ["maxCapacity"];
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Patch,
                $"/api/v1/sessions/{waitlistSessionId:D}",
                JsonContent.Create(new { clear = waitlistClearMaxCapacity, ifMatch = waitlistVersion })),
            HttpStatusCode.UnprocessableEntity,
            "waitlist_requires_capacity");

        // session_invites_closed (422): invitations close at the session start (§12-8, D-081).
        SessionHttpUser inviteClosedFriend = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, inviteClosedFriend.Id);

        Instant inviteClosedStart = _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(20);
        HttpResponseMessage inviteClosedCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Invites closed",
                startTime = SessionEndpointTestSupport.Iso(inviteClosedStart),
                minAttendance = 1,
                isPrivate = true,
            }));
        Assert.Equal(HttpStatusCode.Created, inviteClosedCreated.StatusCode);
        Guid inviteClosedSessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(inviteClosedCreated))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        _factory.Clock.Advance(Duration.FromMinutes(25));

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/sessions/{inviteClosedSessionId:D}/invites",
                JsonContent.Create(new { userIds = new[] { inviteClosedFriend.Id.Value } })),
            HttpStatusCode.UnprocessableEntity,
            "session_invites_closed");

        // session_responses_closed (422): invitation responses close at the session start (D-081/D-089).
        SessionHttpUser responseClosedFriend = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, responseClosedFriend.Id);
        using SessionHttpSession responseClosedFriendSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, responseClosedFriend.Id);

        Instant responseClosedStart = _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(20);
        HttpResponseMessage responseClosedCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Responses closed",
                startTime = SessionEndpointTestSupport.Iso(responseClosedStart),
                minAttendance = 1,
                isPrivate = true,
                inviteeUserIds = new[] { responseClosedFriend.Id.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, responseClosedCreated.StatusCode);
        Guid responseClosedSessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(responseClosedCreated))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        _factory.Clock.Advance(Duration.FromMinutes(25));

        await SessionEndpointTestSupport.AssertProblemAsync(
            await responseClosedFriendSession.SendUnsafeAsync(
                HttpMethod.Put,
                $"/api/v1/sessions/{responseClosedSessionId:D}/my-participation",
                JsonContent.Create(new { response = "Going" })),
            HttpStatusCode.UnprocessableEntity,
            "session_responses_closed");

        // session_leave_closed (422): leaving closes at the session start (§12-3).
        SessionHttpUser leaveClosedFriend = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, leaveClosedFriend.Id);
        using SessionHttpSession leaveClosedFriendSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, leaveClosedFriend.Id);

        Instant leaveClosedStart = _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(20);
        HttpResponseMessage leaveClosedCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Leave closed",
                startTime = SessionEndpointTestSupport.Iso(leaveClosedStart),
                minAttendance = 1,
                isPrivate = true,
                inviteeUserIds = new[] { leaveClosedFriend.Id.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, leaveClosedCreated.StatusCode);
        Guid leaveClosedSessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(leaveClosedCreated))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.OK,
            (await leaveClosedFriendSession.SendUnsafeAsync(
                HttpMethod.Put,
                $"/api/v1/sessions/{leaveClosedSessionId:D}/my-participation",
                JsonContent.Create(new { response = "Going" }))).StatusCode);

        _factory.Clock.Advance(Duration.FromMinutes(25));

        await SessionEndpointTestSupport.AssertProblemAsync(
            await leaveClosedFriendSession.SendUnsafeAsync(
                HttpMethod.Delete,
                $"/api/v1/sessions/{leaveClosedSessionId:D}/my-participation"),
            HttpStatusCode.UnprocessableEntity,
            "session_leave_closed");

        // session_guest_removal_closed (422): guest removal is only rejected once the roster is no
        // longer mutable — terminal (or post-end) sessions share the same code as the live window.
        Instant guestRemovalStart = _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(20);
        HttpResponseMessage guestRemovalCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Guest removal closed",
                startTime = SessionEndpointTestSupport.Iso(guestRemovalStart),
                durationMinutes = 15,
                minAttendance = 1,
                isPrivate = true,
                guestLinkCount = 1,
            }));
        Assert.Equal(HttpStatusCode.Created, guestRemovalCreated.StatusCode);
        using JsonDocument guestRemovalBody = await SessionEndpointTestSupport.SuccessJsonAsync(guestRemovalCreated);
        Guid guestRemovalSessionId = guestRemovalBody.RootElement.GetProperty("session").GetProperty("id").GetGuid();
        string guestRemovalUrl = guestRemovalBody.RootElement.GetProperty("guestLinks")[0].GetProperty("url").GetString()!;
        string guestRemovalToken = guestRemovalUrl[(guestRemovalUrl.LastIndexOf('/') + 1)..];

        using HttpClient guestRemovalExchangeClient = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
                BaseAddress = new Uri("https://localhost"),
            });
        HttpResponseMessage guestRemovalExchange = await guestRemovalExchangeClient.GetAsync($"/g/{guestRemovalToken}");
        SetCookieHeaderValue guestRemovalCookie = GuestExchangeTests.SetCookiesOf(guestRemovalExchange)
            .Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        Guid guestRemovalLinkId = Guid.Parse(guestRemovalCookie.Name.Value!["pu.g.".Length..]);
        HttpResponseMessage guestRemovalClaim = await AntiforgeryTestClient.SendAsync(
            guestRemovalExchangeClient,
            HttpMethod.Post,
            $"/api/v1/guest/{guestRemovalLinkId:N}/claim",
            JsonContent.Create(new { displayName = "Guest To Remove", response = "Going" }),
            GuestExchangeTests.CookieHeader(guestRemovalCookie));
        Assert.Equal(HttpStatusCode.OK, guestRemovalClaim.StatusCode);

        using JsonDocument guestRemovalDetail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{guestRemovalSessionId:D}"));
        Guid guestRemovalParticipationId = guestRemovalDetail.RootElement.GetProperty("roster").EnumerateArray()
            .Single(entry => entry.TryGetProperty("guestIdentityId", out JsonElement gid)
                && gid.ValueKind != JsonValueKind.Null)
            .GetProperty("participationId").GetGuid();

        _factory.Clock.Advance(Duration.FromMinutes(40));
        await SessionEndpointTestSupport.RunDueSweepAsync(_factory);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Delete,
                $"/api/v1/sessions/{guestRemovalSessionId:D}/participations/{guestRemovalParticipationId:D}/guest"),
            HttpStatusCode.UnprocessableEntity,
            "session_guest_removal_closed");

        // join_request_exists (409): one open join request per user per session.
        SessionHttpUser joinExistsFriend = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser joinExistsFiller = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, joinExistsFriend.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, joinExistsFiller.Id);
        using SessionHttpSession joinExistsFriendSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, joinExistsFriend.Id);

        HttpResponseMessage joinExistsCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Join request exists",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                minAttendance = 1,
                isPrivate = false,
                // D-099: a non-private session needs at least one directly invited friend at creation.
                inviteeUserIds = new[] { joinExistsFiller.Id.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, joinExistsCreated.StatusCode);
        Guid joinExistsSessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(joinExistsCreated))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.Created,
            (await joinExistsFriendSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/sessions/{joinExistsSessionId:D}/join-requests")).StatusCode);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await joinExistsFriendSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/sessions/{joinExistsSessionId:D}/join-requests"),
            HttpStatusCode.Conflict,
            "join_request_exists");

        // join_request_not_pending (409): approving an already-approved request a second time.
        SessionHttpUser notPendingFriend = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser notPendingFiller = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, notPendingFriend.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, notPendingFiller.Id);
        using SessionHttpSession notPendingFriendSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, notPendingFriend.Id);

        HttpResponseMessage notPendingCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Join request not pending",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                minAttendance = 1,
                isPrivate = false,
                // D-099: a non-private session needs at least one directly invited friend at creation.
                inviteeUserIds = new[] { notPendingFiller.Id.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, notPendingCreated.StatusCode);
        Guid notPendingSessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(notPendingCreated))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        HttpResponseMessage notPendingRequested = await notPendingFriendSession.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/sessions/{notPendingSessionId:D}/join-requests");
        Assert.Equal(HttpStatusCode.Created, notPendingRequested.StatusCode);
        Guid notPendingRequestId = (await SessionEndpointTestSupport.SuccessJsonAsync(notPendingRequested))
            .RootElement.GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.OK,
            (await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/sessions/{notPendingSessionId:D}/join-requests/{notPendingRequestId:D}/approve")).StatusCode);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/sessions/{notPendingSessionId:D}/join-requests/{notPendingRequestId:D}/approve"),
            HttpStatusCode.Conflict,
            "join_request_not_pending");

        // join_request_closed (422): requests close once the session is no longer roster-mutable
        // (D-173 keeps them open in progress; this reaches the same code once terminal).
        SessionHttpUser joinClosedFriend = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser joinClosedFiller = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, joinClosedFriend.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, joinClosedFiller.Id);
        using SessionHttpSession joinClosedFriendSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, joinClosedFriend.Id);

        HttpResponseMessage joinClosedCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Join request closed",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromMinutes(20)),
                durationMinutes = 15,
                // D-099: a non-private session needs at least one directly invited friend at creation.
                inviteeUserIds = new[] { joinClosedFiller.Id.Value },
                minAttendance = 1,
                isPrivate = false,
            }));
        Assert.Equal(HttpStatusCode.Created, joinClosedCreated.StatusCode);
        Guid joinClosedSessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(joinClosedCreated))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        _factory.Clock.Advance(Duration.FromMinutes(40));
        await SessionEndpointTestSupport.RunDueSweepAsync(_factory);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await joinClosedFriendSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/sessions/{joinClosedSessionId:D}/join-requests"),
            HttpStatusCode.UnprocessableEntity,
            "join_request_closed");

        // guest_link_claimed (409): a second claim of an already-claimed guest link.
        Instant claimedTwiceStart = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage claimedTwiceCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Guest link claimed twice",
                startTime = SessionEndpointTestSupport.Iso(claimedTwiceStart),
                minAttendance = 1,
                isPrivate = true,
                guestLinkCount = 1,
            }));
        Assert.Equal(HttpStatusCode.Created, claimedTwiceCreated.StatusCode);
        using JsonDocument claimedTwiceBody = await SessionEndpointTestSupport.SuccessJsonAsync(claimedTwiceCreated);
        string claimedTwiceUrl = claimedTwiceBody.RootElement.GetProperty("guestLinks")[0].GetProperty("url").GetString()!;
        string claimedTwiceToken = claimedTwiceUrl[(claimedTwiceUrl.LastIndexOf('/') + 1)..];

        using HttpClient claimedTwiceClient = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
                BaseAddress = new Uri("https://localhost"),
            });
        HttpResponseMessage claimedTwiceExchange = await claimedTwiceClient.GetAsync($"/g/{claimedTwiceToken}");
        SetCookieHeaderValue claimedTwiceCookie = GuestExchangeTests.SetCookiesOf(claimedTwiceExchange)
            .Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        Guid claimedTwiceLinkId = Guid.Parse(claimedTwiceCookie.Name.Value!["pu.g.".Length..]);
        string claimedTwiceCookieHeader = GuestExchangeTests.CookieHeader(claimedTwiceCookie);

        Assert.Equal(
            HttpStatusCode.OK,
            (await AntiforgeryTestClient.SendAsync(
                claimedTwiceClient,
                HttpMethod.Post,
                $"/api/v1/guest/{claimedTwiceLinkId:N}/claim",
                JsonContent.Create(new { displayName = "Guest One", response = "Pending" }),
                claimedTwiceCookieHeader)).StatusCode);

        HttpResponseMessage secondClaim = await AntiforgeryTestClient.SendAsync(
            claimedTwiceClient,
            HttpMethod.Post,
            $"/api/v1/guest/{claimedTwiceLinkId:N}/claim",
            JsonContent.Create(new { displayName = "Guest Two", response = "Pending" }),
            claimedTwiceCookieHeader);
        await SessionEndpointTestSupport.AssertProblemAsync(secondClaim, HttpStatusCode.Conflict, "guest_link_claimed");

        // reminder_not_eligible (422): a Declined row is not eligible for a reminder.
        SessionHttpUser reminderFriend = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, reminderFriend.Id);
        using SessionHttpSession reminderFriendSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, reminderFriend.Id);

        HttpResponseMessage reminderCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Reminder not eligible",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                minAttendance = 1,
                isPrivate = true,
                inviteeUserIds = new[] { reminderFriend.Id.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, reminderCreated.StatusCode);
        Guid reminderSessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(reminderCreated))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.OK,
            (await reminderFriendSession.SendUnsafeAsync(
                HttpMethod.Put,
                $"/api/v1/sessions/{reminderSessionId:D}/my-participation",
                JsonContent.Create(new { response = "Declined" }))).StatusCode);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await reminderFriendSession.SendUnsafeAsync(
                HttpMethod.Put,
                $"/api/v1/sessions/{reminderSessionId:D}/my-reminder",
                JsonContent.Create(new { choice = "Hours1" })),
            HttpStatusCode.UnprocessableEntity,
            "reminder_not_eligible");

        // guest_link_terminal (409, A-074 branch b): a mutation against a revoked guest link.
        Instant terminalGuestStart = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage terminalGuestCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Guest link terminal",
                startTime = SessionEndpointTestSupport.Iso(terminalGuestStart),
                minAttendance = 1,
                isPrivate = true,
                guestLinkCount = 1,
            }));
        Assert.Equal(HttpStatusCode.Created, terminalGuestCreated.StatusCode);
        using JsonDocument terminalGuestBody = await SessionEndpointTestSupport.SuccessJsonAsync(terminalGuestCreated);
        Guid terminalGuestSessionId = terminalGuestBody.RootElement.GetProperty("session").GetProperty("id").GetGuid();
        string terminalGuestUrl = terminalGuestBody.RootElement.GetProperty("guestLinks")[0].GetProperty("url").GetString()!;
        string terminalGuestToken = terminalGuestUrl[(terminalGuestUrl.LastIndexOf('/') + 1)..];

        using HttpClient terminalGuestClient = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
                BaseAddress = new Uri("https://localhost"),
            });
        HttpResponseMessage terminalGuestExchange = await terminalGuestClient.GetAsync($"/g/{terminalGuestToken}");
        SetCookieHeaderValue terminalGuestCookie = GuestExchangeTests.SetCookiesOf(terminalGuestExchange)
            .Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        Guid terminalGuestLinkId = Guid.Parse(terminalGuestCookie.Name.Value!["pu.g.".Length..]);
        string terminalGuestCookieHeader = GuestExchangeTests.CookieHeader(terminalGuestCookie);

        await using (AsyncServiceScope revokeScope = _factory.Services.CreateAsyncScope())
        {
            var invitations = revokeScope.ServiceProvider.GetRequiredService<SessionInvitationUseCases>();
            SessionCommandResult revoked = await invitations.RevokeGuestLinkAsync(
                SessionId.From(terminalGuestSessionId), organizer.Id, GuestLinkId.From(terminalGuestLinkId), default);
            Assert.True(revoked.Succeeded);
        }

        HttpResponseMessage terminalGuestMutation = await AntiforgeryTestClient.SendAsync(
            terminalGuestClient,
            HttpMethod.Post,
            $"/api/v1/guest/{terminalGuestLinkId:N}/claim",
            JsonContent.Create(new { displayName = "Late Guest", response = "Going" }),
            terminalGuestCookieHeader);
        await SessionEndpointTestSupport.AssertProblemAsync(
            terminalGuestMutation, HttpStatusCode.Conflict, "guest_link_terminal");

        // Closure: every code this test genuinely reproduces here, plus the codes proven by sibling
        // tests in this file, plus documented untestable-over-HTTP residuals, must equal the exact
        // documented Phase 4 problem-code inventory (PartyUpOpenApiTransformer.ProblemCodes) — so a
        // future addition to that inventory fails this test until it lands in one of the three sets.
        HashSet<string> exercisedInThisTest = new(StringComparer.Ordinal)
        {
            "session_not_found",
            "invalid_scope",
            "invalid_cursor",
            "session_validation",
            "session_time_locked",
            "capacity_below_occupancy",
            "waitlist_requires_capacity",
            "session_invites_closed",
            "session_responses_closed",
            "session_leave_closed",
            "session_guest_removal_closed",
            "join_request_exists",
            "join_request_not_pending",
            "join_request_closed",
            "guest_link_claimed",
            "reminder_not_eligible",
            "guest_link_terminal",
        };
        HashSet<string> exercisedBySiblingTestsInThisFile = new(StringComparer.Ordinal)
        {
            "invalid_if_match", // MalformedIfMatchIs400AndStaleIfMatchIs409_A071
            "session_conflict", // MalformedIfMatchIs400AndStaleIfMatchIs409_A071 / ConcurrentEditsWithTheSameInitialVersion_ExactlyOneWinsUnderLock_A071
            "session_terminal", // CancelIsIdempotentAndCompletedIsCancelable_D156_D157
            "not_waitlisted", // PromoteAndRejectWaitlistRequireOrganizerAndAWaitlistedRow_D084
            "no_session_capacity", // PromoteAndRejectWaitlistRequireOrganizerAndAWaitlistedRow_D084
        };
        // transient_conflict needs a genuine SQL 40001/40P01 retry-exhaustion under contention; not
        // deterministically reproducible over HTTP, so it stays an accepted untested residual.
        HashSet<string> acceptedUntestedResiduals = new(StringComparer.Ordinal) { "transient_conflict" };

        HashSet<string> covered = new(exercisedInThisTest, StringComparer.Ordinal);
        covered.UnionWith(exercisedBySiblingTestsInThisFile);
        covered.UnionWith(acceptedUntestedResiduals);

        Assert.Equal(
            Phase4DocumentedProblemCodes().OrderBy(code => code, StringComparer.Ordinal),
            covered.OrderBy(code => code, StringComparer.Ordinal));
    }

    /// <summary>
    /// Reflects <see cref="PartyUpOpenApiTransformer"/>'s private problem-code registry so the closure
    /// assertion above tracks the OpenAPI-declared inventory itself rather than a second, driftable
    /// hand-maintained copy.
    /// </summary>
    private static string[] Phase4DocumentedProblemCodes()
    {
        FieldInfo field = typeof(PartyUpOpenApiTransformer).GetField(
            "ProblemCodes", BindingFlags.NonPublic | BindingFlags.Static)!;
        var allCodes = (string[])field.GetValue(null)!;
        int phase4Start = Array.IndexOf(allCodes, "session_not_found");
        return allCodes[phase4Start..];
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
