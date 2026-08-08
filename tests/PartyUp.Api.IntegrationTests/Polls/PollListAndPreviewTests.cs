using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Jobs;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// ticket-P6-S4c: <c>GET /polls</c>'s two bands (PHASE6_DESIGN.md §4.1), <c>GET /{id}/finalization-preview</c>
/// (§4, design §5 "Preview guarantee"), and the property that ties them together -- preview and
/// finalize share one allocation function, so a preview taken and then finalized on the same option
/// against unchanged inputs must produce an identical roster/waitlist split.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollListAndPreviewTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    // ---- involved band ----------------------------------------------------------------------------

    [Fact]
    public async Task Involved_ShowsOrganizerAndParticipant_HidesAStranger()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-inv-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"pl-inv-friend-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"pl-inv-stranger-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);

        using JsonDocument organizerList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync("/api/v1/polls?band=involved"));
        Assert.Contains(
            organizerList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);

        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        using JsonDocument friendList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await friendSession.GetAsync("/api/v1/polls?band=involved"));
        Assert.Contains(
            friendList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);

        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);
        using JsonDocument strangerList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await strangerSession.GetAsync("/api/v1/polls?band=involved"));
        Assert.DoesNotContain(
            strangerList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);
    }

    [Fact]
    public async Task Involved_ExcludesCanceledAndFinalized()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-inv-term-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        Guid canceledPollId = await CreatePollAsync(organizerSession, []);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/polls/{canceledPollId:D}/cancel")).StatusCode);

        (Guid finalizedPollId, Guid optionId) = await CreatePollWithFirstOptionAsync(organizerSession);
        _factory.Clock.Advance(Duration.FromHours(25));
        Assert.Equal(
            HttpStatusCode.OK,
            (await organizerSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/polls/{finalizedPollId:D}/finalize", JsonContent.Create(new { optionId }))).StatusCode);

        using JsonDocument list = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync("/api/v1/polls?band=involved"));
        var ids = list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
        Assert.DoesNotContain(canceledPollId, ids);
        Assert.DoesNotContain(finalizedPollId, ids);
    }

    // ---- discoverable band --------------------------------------------------------------------------

    [Fact]
    public async Task Discoverable_ShowsANonPrivatePollThroughAFriendOfTheOrganizer_HidesAPrivateOne()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-disc-org-{Guid.NewGuid():N}@example.test");
        UserId invitedFriendId = await _factory.SeedUserAsync(email: $"pl-disc-invited-{Guid.NewGuid():N}@example.test");
        UserId discovererId = await _factory.SeedUserAsync(email: $"pl-disc-friend-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"pl-disc-stranger-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, invitedFriendId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, discovererId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid nonPrivatePollId = await CreateNonPrivatePollAsync(organizerSession, invitedFriendId);
        Guid privatePollId = await CreatePollAsync(organizerSession, []);

        // The rule-2 discoverer is a friend of the organizer but never invited (unattached) --
        // exactly the D-118 entry point, distinct from the already-attached invitee.
        using SessionHttpSession discovererSession = await SessionEndpointTestSupport.SignInAsync(_factory, discovererId);
        using JsonDocument discovererList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await discovererSession.GetAsync("/api/v1/polls?band=discoverable"));
        var discovererIds = discovererList.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(nonPrivatePollId, discovererIds);
        Assert.DoesNotContain(privatePollId, discovererIds);

        // The invited friend is already attached (rule-1) -- discover excludes them the same way
        // ListDiscoverAsync excludes an already-attached session participant.
        using SessionHttpSession invitedSession = await SessionEndpointTestSupport.SignInAsync(_factory, invitedFriendId);
        using JsonDocument invitedList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await invitedSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.DoesNotContain(
            invitedList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == nonPrivatePollId);

        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);
        using JsonDocument strangerList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await strangerSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.DoesNotContain(
            strangerList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == nonPrivatePollId);
    }

    [Fact]
    public async Task Discoverable_ExcludesAClosedPoll()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-disc-closed-org-{Guid.NewGuid():N}@example.test");
        UserId invitedFriendId = await _factory.SeedUserAsync(email: $"pl-disc-closed-invited-{Guid.NewGuid():N}@example.test");
        UserId discovererId = await _factory.SeedUserAsync(email: $"pl-disc-closed-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, invitedFriendId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, discovererId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreateNonPrivatePollAsync(organizerSession, invitedFriendId);
        _factory.Clock.Advance(Duration.FromHours(25));

        // The materialized status column only flips on a write path or the due-sweep job -- a plain
        // list read never re-derives it (ARCHITECTURE's materialized-status invariant), so the sweep
        // runs here the same way PollSweepTests does.
        await using (Microsoft.Extensions.DependencyInjection.AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<PollDueSweepJob>();
            await job.ExecuteAsync(default);
        }

        using SessionHttpSession discovererSession = await SessionEndpointTestSupport.SignInAsync(_factory, discovererId);
        using JsonDocument discovererList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await discovererSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.DoesNotContain(
            discovererList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);
    }

    // ---- cursor ----------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidBand_Is400()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"pl-band-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await session.GetAsync("/api/v1/polls?band=nonsense"),
            HttpStatusCode.BadRequest,
            "poll_validation");
    }

    [Fact]
    public async Task CrossBandCursor_IsRejected()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-cursor-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        await CreatePollAsync(organizerSession, []);

        using JsonDocument involvedPage = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync("/api/v1/polls?band=involved"));

        // A page short of the fixed page size mints no next cursor -- seed one directly against the
        // shared SessionListCursor machinery (SessionListScope.PollsInvolved) the same way the
        // session list's own cross-scope test does, rather than paging through twenty polls.
        string involvedCursor = new PartyUp.Application.Sessions.SessionListCursor(
            _factory.Clock.GetCurrentInstant(), Guid.NewGuid(), PartyUp.Application.Sessions.SessionListScope.PollsInvolved).Encode();

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.GetAsync($"/api/v1/polls?band=discoverable&cursor={Uri.EscapeDataString(involvedCursor)}"),
            HttpStatusCode.BadRequest,
            "invalid_cursor");

        Assert.True(involvedPage.RootElement.TryGetProperty("nextCursor", out _));
    }

    // ---- finalization preview ---------------------------------------------------------------------

    [Fact]
    public async Task Preview_PreDeadlineIs409_InWindowIs200_PostWindowIs422()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-preview-window-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        (Guid earlyPollId, _) = await CreatePollWithFirstOptionAsync(organizerSession);
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{earlyPollId:D}/finalization-preview"),
            HttpStatusCode.Conflict,
            "poll_invalid_transition");

        (Guid onTimePollId, _) = await CreatePollWithFirstOptionAsync(organizerSession);
        _factory.Clock.Advance(Duration.FromHours(25));
        HttpResponseMessage onTimePreview = await organizerSession.GetAsync(
            $"/api/v1/polls/{onTimePollId:D}/finalization-preview");
        Assert.Equal(HttpStatusCode.OK, onTimePreview.StatusCode);
        using JsonDocument onTimeBody = await SessionEndpointTestSupport.SuccessJsonAsync(onTimePreview);
        Assert.Equal(2, onTimeBody.RootElement.GetProperty("options").GetArrayLength());

        (Guid latePollId, _) = await CreatePollWithFirstOptionAsync(organizerSession);
        _factory.Clock.Advance(Duration.FromHours(73));
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{latePollId:D}/finalization-preview"),
            HttpStatusCode.UnprocessableEntity,
            "poll_selection_window_closed");
    }

    [Fact]
    public async Task Preview_NonOrganizerIsPollNotFound()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-preview-nf-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"pl-preview-nf-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);
        _factory.Clock.Advance(Duration.FromHours(25));

        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.GetAsync($"/api/v1/polls/{pollId:D}/finalization-preview"),
            HttpStatusCode.NotFound,
            "poll_not_found");
    }

    /// <summary>
    /// The preview guarantee (design §5): calling preview then finalizing the same option, against
    /// unchanged inputs, must yield the identical roster/waitlist split -- because both routes call
    /// the same <c>Poll.Finalize</c> allocation.
    /// </summary>
    [Fact]
    public async Task Preview_MatchesTheFinalizedSessionRoster_ForTheSameOption()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-preview-eq-org-{Guid.NewGuid():N}@example.test");
        UserId availableId = await _factory.SeedUserAsync(email: $"pl-preview-eq-avail-{Guid.NewGuid():N}@example.test");
        UserId maybeId = await _factory.SeedUserAsync(email: $"pl-preview-eq-maybe-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, availableId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, maybeId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        (Guid pollId, Guid optionId) = await CreatePollWithFirstOptionAsync(organizerSession, [availableId, maybeId]);

        using SessionHttpSession availableSession = await SessionEndpointTestSupport.SignInAsync(_factory, availableId);
        Assert.Equal(
            HttpStatusCode.OK,
            (await availableSession.SendUnsafeAsync(
                HttpMethod.Put, $"/api/v1/polls/{pollId:D}/options/{optionId:D}/vote", JsonContent.Create(new { choice = "available" })))
                .StatusCode);

        using SessionHttpSession maybeSession = await SessionEndpointTestSupport.SignInAsync(_factory, maybeId);
        Assert.Equal(
            HttpStatusCode.OK,
            (await maybeSession.SendUnsafeAsync(
                HttpMethod.Put, $"/api/v1/polls/{pollId:D}/options/{optionId:D}/vote", JsonContent.Create(new { choice = "maybe" })))
                .StatusCode);

        _factory.Clock.Advance(Duration.FromHours(25));

        using JsonDocument previewBody = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}/finalization-preview"));
        JsonElement previewOption = previewBody.RootElement.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("optionId").GetGuid() == optionId);
        var previewRoster = previewOption.GetProperty("roster").EnumerateArray()
            .Where(r => r.TryGetProperty("userId", out _))
            .Select(r => (UserId: r.GetProperty("userId").GetGuid(), Status: r.GetProperty("status").GetString(), IsWaitlisted: r.GetProperty("isWaitlisted").GetBoolean()))
            .OrderBy(r => r.UserId)
            .ToArray();

        HttpResponseMessage finalize = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/polls/{pollId:D}/finalize", JsonContent.Create(new { optionId }));
        Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
        using JsonDocument finalizeBody = await SessionEndpointTestSupport.SuccessJsonAsync(finalize);
        Guid sessionId = finalizeBody.RootElement.GetProperty("sessionId").GetGuid();

        using JsonDocument sessionBody = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        var finalizedRoster = sessionBody.RootElement.GetProperty("roster").EnumerateArray()
            .Where(r => r.TryGetProperty("userId", out _))
            .Select(r => (UserId: r.GetProperty("userId").GetGuid(), Status: r.GetProperty("status").GetString(), IsWaitlisted: r.GetProperty("isWaitlisted").GetBoolean()))
            .OrderBy(r => r.UserId)
            .ToArray();

        Assert.Equal(3, previewRoster.Length); // organizer + available + maybe
        Assert.Equal(previewRoster, finalizedRoster);
    }

    // ---- fixture helpers ----------------------------------------------------------------------------

    private async Task<Guid> CreatePollAsync(SessionHttpSession organizerSession, IReadOnlyList<UserId> inviteeIds)
    {
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "List/preview fixture poll",
                optionStartTimes = new[]
                {
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(2)),
                },
                isPrivate = inviteeIds.Count == 0,
                minAttendance = 1,
                inviteeUserIds = inviteeIds.Select(id => id.Value).ToArray(),
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        return body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
    }

    /// <summary>D-125: a non-private poll requires >=1 directly invited registered friend at creation.</summary>
    private async Task<Guid> CreateNonPrivatePollAsync(SessionHttpSession organizerSession, UserId inviteeId)
    {
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Discoverable fixture poll",
                optionStartTimes = new[]
                {
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(2)),
                },
                isPrivate = false,
                minAttendance = 1,
                inviteeUserIds = new[] { inviteeId.Value },
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        return body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
    }

    private async Task<(Guid PollId, Guid OptionId)> CreatePollWithFirstOptionAsync(
        SessionHttpSession organizerSession, IReadOnlyList<UserId>? inviteeIds = null)
    {
        Guid pollId = await CreatePollAsync(organizerSession, inviteeIds ?? []);
        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        Guid optionId = detail.RootElement.GetProperty("options")[0].GetProperty("id").GetGuid();
        return (pollId, optionId);
    }
}
