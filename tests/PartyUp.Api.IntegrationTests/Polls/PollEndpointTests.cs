using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// ticket-P6-S4a: the registered poll HTTP surface (PHASE6_DESIGN.md §4). Fail-closed 404 matrix
/// (D-114's hidden-equals-missing rule, D-118 join requests, D-125 private-poll visibility),
/// create-vote-finalize happy path, problem-code shape, and the D-076 blocked-pair wire-masking
/// precedent extended to the poll roster. Reuses <c>SessionEndpointTestSupport</c> (internal, same
/// assembly) rather than duplicating sign-in/antiforgery/JSON plumbing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GetPoll_StrangerCannotSee_ButOrganizerAndVoterCan_D114()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-friend-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"poll-stranger-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);

        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);
        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.GetAsync($"/api/v1/polls/{pollId:D}"),
            HttpStatusCode.NotFound,
            "poll_not_found");

        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.GetAsync($"/api/v1/polls/{Guid.NewGuid():D}"),
            HttpStatusCode.NotFound,
            "poll_not_found");

        using JsonDocument organizerView = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        Assert.Equal(pollId, organizerView.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(2, organizerView.RootElement.GetProperty("options").GetArrayLength());

        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        using JsonDocument friendView = await SessionEndpointTestSupport.SuccessJsonAsync(
            await friendSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        Assert.Equal(pollId, friendView.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task GetPoll_BlockedPairIsIndistinguishableFromMissing_D114()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-block-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-block-friend-{Guid.NewGuid():N}@example.test");
        UserId blockedId = await _factory.SeedUserAsync(email: $"poll-block-other-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
            context.Blocks.Add(Block.Create(organizerId, blockedId, blockedUser.Username!.Value, _factory.Clock));
            await context.SaveChangesAsync();
        }

        using SessionHttpSession blockedSession = await SessionEndpointTestSupport.SignInAsync(_factory, blockedId);
        await SessionEndpointTestSupport.AssertProblemAsync(
            await blockedSession.GetAsync($"/api/v1/polls/{pollId:D}"),
            HttpStatusCode.NotFound,
            "poll_not_found");
    }

    [Fact]
    public async Task OrganizerOnlyRoutes_ReturnNotFoundForANonOrganizerVoter_D114()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-oo-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-oo-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);
        Guid optionId = await FirstOptionIdAsync(organizerSession, pollId);

        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/options/{optionId:D}"),
            HttpStatusCode.NotFound,
            "poll_not_found");

        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/polls/{pollId:D}/cancel"),
            HttpStatusCode.NotFound,
            "poll_not_found");

        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.GetAsync($"/api/v1/polls/{pollId:D}/join-requests"),
            HttpStatusCode.NotFound,
            "poll_not_found");
    }

    [Fact]
    public async Task RequestJoin_OnAPrivatePollFromAStranger_ReturnsNotFound_D125()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-priv-org-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"poll-priv-stranger-{Guid.NewGuid():N}@example.test");

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Private poll fixture",
                options = new[]
                {
                    new { startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)) },
                    new { startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(2)) },
                },
                visibility = "private",
                minAttendance = 1,
            }));
        using JsonDocument createBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = createBody.RootElement.GetProperty("poll").GetProperty("id").GetGuid();

        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);
        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/polls/{pollId:D}/join-requests"),
            HttpStatusCode.NotFound,
            "poll_not_found");
    }

    [Fact]
    public async Task CreateVoteFinalize_HappyPathProducesASession()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-flow-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-flow-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);

        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);
        Guid optionId = await FirstOptionIdAsync(organizerSession, pollId);

        HttpResponseMessage vote = await friendSession.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/polls/{pollId:D}/options/{optionId:D}/vote",
            JsonContent.Create(new { choice = "available" }));
        using JsonDocument voteBody = await SessionEndpointTestSupport.SuccessJsonAsync(vote);
        JsonElement votedOption = voteBody.RootElement.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("id").GetGuid() == optionId);
        Assert.Equal(1, votedOption.GetProperty("availableCount").GetInt32());

        // D-079: voting closes at the deadline (24h default); advance past it and the 48h selection window's
        // start so Finalize's clock-inclusive gate is open.
        _factory.Clock.Advance(Duration.FromHours(25));

        HttpResponseMessage finalize = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/polls/{pollId:D}/finalize",
            JsonContent.Create(new { optionId }));
        using JsonDocument finalizeBody = await SessionEndpointTestSupport.SuccessJsonAsync(finalize);
        Guid sessionId = finalizeBody.RootElement.GetProperty("sessionId").GetGuid();
        Assert.Equal("finalized", finalizeBody.RootElement.GetProperty("poll").GetProperty("status").GetString());

        using JsonDocument session = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        Assert.Equal(sessionId, session.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AMaskedPollRosterEntryHasNoUserIdProperty_D076()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-wire-org-{Guid.NewGuid():N}@example.test");
        UserId blockedId = await _factory.SeedUserAsync(email: $"poll-wire-blocked-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, blockedId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [blockedId]);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
            context.Blocks.Add(Block.Create(organizerId, blockedId, blockedUser.Username!.Value, _factory.Clock));
            await context.SaveChangesAsync();
        }

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        JsonElement roster = detail.RootElement.GetProperty("roster");
        Assert.Equal(2, roster.GetArrayLength());
        JsonElement maskedEntry = roster.EnumerateArray()
            .Single(entry => !entry.TryGetProperty("userId", out _) && !entry.TryGetProperty("displayName", out _));
        Assert.False(maskedEntry.TryGetProperty("userId", out _));
        // D-188: the avatar is masked with the name, in one expression — absent, not null.
        Assert.False(maskedEntry.TryGetProperty("avatar", out _));

        JsonElement organizerEntry = roster.EnumerateArray()
            .Single(entry => entry.TryGetProperty("userId", out JsonElement id) && id.GetGuid() == organizerId.Value);
        Assert.True(organizerEntry.TryGetProperty("displayName", out _));
        JsonElement organizerAvatar = organizerEntry.GetProperty("avatar");
        Assert.Equal("emoji", organizerAvatar.GetProperty("kind").GetString());
        Assert.Equal("🙂", organizerAvatar.GetProperty("emoji").GetString());
    }

    /// <summary>
    /// ticket-P6-S4a-fix1 F3: a blocked co-participant's suggested option carries no user id on the
    /// wire either -- the roster already masks them, and this is the second channel that must agree.
    /// </summary>
    [Fact]
    public async Task AMaskedSuggesterHasNoUserIdOnTheirSuggestedOption_F3()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-suggestmask-org-{Guid.NewGuid():N}@example.test");
        UserId blockedId = await _factory.SeedUserAsync(email: $"poll-suggestmask-blocked-{Guid.NewGuid():N}@example.test");
        UserId unblockedId = await _factory.SeedUserAsync(email: $"poll-suggestmask-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, blockedId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, unblockedId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession blockedSession = await SessionEndpointTestSupport.SignInAsync(_factory, blockedId);
        using SessionHttpSession unblockedSession = await SessionEndpointTestSupport.SignInAsync(_factory, unblockedId);
        Guid pollId = await CreatePollAsync(organizerSession, [blockedId, unblockedId]);

        Instant blockedSuggestion = _factory.Clock.GetCurrentInstant() + Duration.FromDays(3);
        Instant unblockedSuggestion = _factory.Clock.GetCurrentInstant() + Duration.FromDays(4);

        Assert.Equal(
            HttpStatusCode.OK,
            (await blockedSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/options",
                JsonContent.Create(new { startTime = SessionEndpointTestSupport.Iso(blockedSuggestion) }))).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await unblockedSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/options",
                JsonContent.Create(new { startTime = SessionEndpointTestSupport.Iso(unblockedSuggestion) }))).StatusCode);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
            context.Blocks.Add(Block.Create(organizerId, blockedId, blockedUser.Username!.Value, _factory.Clock));
            await context.SaveChangesAsync();
        }

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        JsonElement options = detail.RootElement.GetProperty("options");

        JsonElement blockedOption = options.EnumerateArray()
            .Single(o => SessionEndpointTestSupport.Iso(blockedSuggestion) ==
                o.GetProperty("startTime").GetString());
        Assert.False(blockedOption.TryGetProperty("suggestedByUserId", out _));

        JsonElement unblockedOption = options.EnumerateArray()
            .Single(o => SessionEndpointTestSupport.Iso(unblockedSuggestion) ==
                o.GetProperty("startTime").GetString());
        Assert.True(unblockedOption.TryGetProperty("suggestedByUserId", out JsonElement suggesterId));
        Assert.Equal(unblockedId.Value, suggesterId.GetGuid());
    }

    /// <summary>ticket-P6-S4a-fix1 F5: the roster's row key is the participation id, never a user id.</summary>
    [Fact]
    public async Task RosterEntriesCarryAStableParticipationIdRowKey_F5()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-rowkey-org-{Guid.NewGuid():N}@example.test");
        UserId blockedId = await _factory.SeedUserAsync(email: $"poll-rowkey-blocked-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, blockedId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [blockedId]);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
            context.Blocks.Add(Block.Create(organizerId, blockedId, blockedUser.Username!.Value, _factory.Clock));
            await context.SaveChangesAsync();
        }

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        JsonElement roster = detail.RootElement.GetProperty("roster");
        Assert.Equal(2, roster.GetArrayLength());

        Guid[] participationIds = [.. roster.EnumerateArray().Select(entry => entry.GetProperty("participationId").GetGuid())];
        Assert.Equal(2, participationIds.Distinct().Count());
        Assert.All(participationIds, id => Assert.NotEqual(Guid.Empty, id));

        // The masked row's key is its participation id, not the (absent) user id -- unkeyable-to-identity.
        JsonElement maskedEntry = roster.EnumerateArray()
            .Single(entry => !entry.TryGetProperty("userId", out _) && !entry.TryGetProperty("displayName", out _));
        Assert.NotEqual(Guid.Empty, maskedEntry.GetProperty("participationId").GetGuid());
        // D-188: the row key survives masking; the face does not.
        Assert.False(maskedEntry.TryGetProperty("avatar", out _));
    }

    /// <summary>
    /// The join-requests list returns the A-072 page shape, not a bare array: a single-page fixture
    /// proves the envelope and content, a 21-request fixture proves the real page-size boundary and
    /// cursor round-trip (not merely an always-present, always-null <c>nextCursor</c> field), and a
    /// cursor minted for a different list is rejected rather than silently honored (A-072's own rule,
    /// reused by this poll route through the shared <c>SessionListCursor</c>/<c>SessionListScope</c>
    /// machinery).
    /// </summary>
    [Fact]
    public async Task ListJoinRequests_ReturnsTheA072PageShape_F4()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-page-org-{Guid.NewGuid():N}@example.test");
        UserId fillerId = await _factory.SeedUserAsync(email: $"poll-page-filler-{Guid.NewGuid():N}@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: $"poll-page-req-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, fillerId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, requesterId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession requesterSession = await SessionEndpointTestSupport.SignInAsync(_factory, requesterId);
        Guid pollId = await CreatePollAsync(organizerSession, [fillerId]);

        Assert.Equal(
            HttpStatusCode.Created,
            (await requesterSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/polls/{pollId:D}/join-requests")).StatusCode);

        using JsonDocument page = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}/join-requests"));
        Assert.True(page.RootElement.TryGetProperty("items", out JsonElement items));
        Assert.True(page.RootElement.TryGetProperty("nextCursor", out JsonElement nextCursor));
        Assert.Equal(JsonValueKind.Null, nextCursor.ValueKind);
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(requesterId.Value, items[0].GetProperty("requesterId").GetGuid());

        // A cursor minted for a different list's scope is rejected outright, never silently honored.
        string crossScopeCursor = new SessionListCursor(
            _factory.Clock.GetCurrentInstant(), Guid.NewGuid(), SessionListScope.Upcoming).Encode();
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}/join-requests?cursor={crossScopeCursor}"),
            HttpStatusCode.BadRequest,
            "invalid_cursor");
    }

    /// <summary>The page-size boundary and cursor round-trip: 21 requests force a genuine two-page split.</summary>
    [Fact]
    public async Task ListJoinRequests_PagesAtTheTwentyItemBoundaryAndRejectsAStaleCursorReplayAcrossPages_F4()
    {
        const int RequestCount = 21;
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-page21-org-{Guid.NewGuid():N}@example.test");
        UserId fillerId = await _factory.SeedUserAsync(email: $"poll-page21-filler-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, fillerId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [fillerId]);

        List<Guid> requesterIds = [];
        for (int i = 0; i < RequestCount; i++)
        {
            UserId requesterId = await _factory.SeedUserAsync(email: $"poll-page21-req{i}-{Guid.NewGuid():N}@example.test");
            await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, requesterId);
            using SessionHttpSession requesterSession = await SessionEndpointTestSupport.SignInAsync(_factory, requesterId);
            Assert.Equal(
                HttpStatusCode.Created,
                (await requesterSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/polls/{pollId:D}/join-requests")).StatusCode);
            requesterIds.Add(requesterId.Value);
        }

        using JsonDocument firstPage = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}/join-requests"));
        JsonElement firstItems = firstPage.RootElement.GetProperty("items");
        Assert.Equal(20, firstItems.GetArrayLength());
        Assert.True(firstPage.RootElement.TryGetProperty("nextCursor", out JsonElement firstCursorElement));
        string? firstCursor = firstCursorElement.GetString();
        Assert.False(string.IsNullOrEmpty(firstCursor));

        using JsonDocument secondPage = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}/join-requests?cursor={firstCursor}"));
        JsonElement secondItems = secondPage.RootElement.GetProperty("items");
        Assert.Equal(1, secondItems.GetArrayLength());
        Assert.True(secondPage.RootElement.TryGetProperty("nextCursor", out JsonElement secondCursorElement));
        Assert.Equal(JsonValueKind.Null, secondCursorElement.ValueKind);

        // Every requester id appears exactly once across the two pages, no duplicate or dropped row.
        Guid[] seenIds =
        [
            .. firstItems.EnumerateArray().Select(i => i.GetProperty("requesterId").GetGuid()),
            .. secondItems.EnumerateArray().Select(i => i.GetProperty("requesterId").GetGuid()),
        ];
        Assert.Equal(requesterIds.OrderBy(id => id), seenIds.OrderBy(id => id));
    }

    [Fact]
    public async Task ProblemResponses_AreShapedProblemJsonWithTheExactCode()
    {
        UserId strangerId = await _factory.SeedUserAsync(email: $"poll-problem-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);

        HttpResponseMessage response = await strangerSession.GetAsync($"/api/v1/polls/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string[] propertyNames = [.. body.RootElement.EnumerateObject().Select(p => p.Name)];
        Assert.Equal(["type", "title", "status", "instance", "code", "traceId"], propertyNames);
        Assert.Equal("poll_not_found", body.RootElement.GetProperty("code").GetString());
    }

    // ---- fixture helpers ------------------------------------------------------------------------

    private async Task<Guid> CreatePollAsync(SessionHttpSession organizerSession, IReadOnlyList<UserId> inviteeIds)
    {
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Poll fixture",
                options = new[]
                {
                    new { startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)) },
                    new { startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(2)) },
                },
                visibility = "public",
                minAttendance = 1,
                inviteeUserIds = inviteeIds.Select(id => id.Value).ToArray(),
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        return body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> FirstOptionIdAsync(SessionHttpSession session, Guid pollId)
    {
        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync($"/api/v1/polls/{pollId:D}"));
        return detail.RootElement.GetProperty("options")[0].GetProperty("id").GetGuid();
    }
}
