using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.Hosting;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// ticket-P6-S4a-fix1 F1: the closed Phase 6 poll problem-code inventory
/// (<see cref="PartyUpOpenApiTransformer"/>'s <c>ProblemCodes</c> array, the section appended after
/// <c>poll_not_found</c>) — every code the poll HTTP surface can produce, exercised here or by a
/// named sibling test, plus the accepted untested residual and its rationale. Mirrors
/// <c>GameSessionEndpointTests.EveryDocumentedProblemCodeMapsToItsDocumentedStatus</c>'s closure
/// idiom exactly, scoped to the poll section the Phase 4 test no longer swallows.
/// Also covers Ruling A's three distinct finalize/cancel window outcomes and Ruling B's zero-option
/// precondition.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollProblemCodeClosureTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task PollValidation_MalformedSuggestStartTime_Is400()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-code-val-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, []);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/options",
                JsonContent.Create(new { startTime = "not-an-instant" })),
            HttpStatusCode.BadRequest,
            "poll_validation");
    }

    [Fact]
    public async Task PollOptionCap_ThirteenthDistinctSuggestion_Is422_D171()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-code-cap-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);

        // Two at creation, ten more suggested to reach the twelve-option cap (D-080), then a
        // thirteenth distinct instant trips D-171's suggestion-time cap error.
        Guid pollId = await CreatePollAsync(organizerSession, [baseTime, baseTime + Duration.FromHours(1)]);
        for (int i = 2; i < 12; i++)
        {
            HttpResponseMessage suggested = await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/options",
                JsonContent.Create(new { startTime = SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(i)) }));
            Assert.Equal(HttpStatusCode.OK, suggested.StatusCode);
        }

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/options",
                JsonContent.Create(new { startTime = SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(12)) })),
            HttpStatusCode.UnprocessableEntity,
            "poll_option_cap");
    }

    [Fact]
    public async Task PollOptionNotFound_DeletingAnUnknownOption_Is404()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-code-optnf-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, []);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/options/{Guid.NewGuid():D}"),
            HttpStatusCode.NotFound,
            "poll_option_not_found");
    }

    [Fact]
    public async Task PollVotingClosed_VotingAfterTheDeadline_Is422_D079()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-code-vc-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-code-vc-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        (Guid pollId, Guid optionId) = await CreatePollWithFirstOptionAsync(organizerSession, [friendId]);

        _factory.Clock.Advance(Duration.FromHours(25));

        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.SendUnsafeAsync(
                HttpMethod.Put,
                $"/api/v1/polls/{pollId:D}/options/{optionId:D}/vote",
                JsonContent.Create(new { choice = "available" })),
            HttpStatusCode.UnprocessableEntity,
            "poll_voting_closed");
    }

    [Fact]
    public async Task PollTerminal_DeletingAnOptionAfterFinalization_Is409()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-code-term-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        (Guid pollId, Guid optionId) = await CreatePollWithFirstOptionAsync(organizerSession, []);

        _factory.Clock.Advance(Duration.FromHours(25));
        HttpResponseMessage finalize = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/polls/{pollId:D}/finalize", JsonContent.Create(new { optionId }));
        Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/options/{optionId:D}"),
            HttpStatusCode.Conflict,
            "poll_terminal");
    }

    [Fact]
    public async Task PollGuestNotFound_RemovingAnUnknownGuest_Is404()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-code-gnf-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, []);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/polls/{pollId:D}/guests/{Guid.NewGuid():D}/remove"),
            HttpStatusCode.NotFound,
            "poll_guest_not_found");
    }

    [Fact]
    public async Task PollConflict_RevokingAClaimedGuestLink_Is409_D150()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-code-conflict-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Guest link conflict fixture",
                options = new[]
                {
                    new { startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)) },
                    new { startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(2)) },
                },
                visibility = "private",
                minAttendance = 1,
                guestLinkCount = 1,
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
        Guid guestLinkId = body.RootElement.GetProperty("guestLinks")[0].GetProperty("guestLinkId").GetGuid();

        // Bypasses the not-yet-shipped poll guest HTTP surface (S4b) to seed a claimed guest,
        // mirroring GameSessionEndpointTests.ClaimGuestDirectlyAsync's precedent.
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var useCase = scope.ServiceProvider.GetRequiredService<GuestPollUseCases>();
            GuestPollCommandResult claimed = await useCase.ClaimGuestLinkAsync(
                PollId.From(pollId), GuestLinkId.From(guestLinkId), DisplayName.Create("Late Claimant"), default);
            Assert.True(claimed.Succeeded, claimed.Code);
        }

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/guest-links/{guestLinkId:D}"),
            HttpStatusCode.Conflict,
            "poll_conflict");
    }

    /// <summary>
    /// A block-and-unblock cycle leaves a poll participant permanently Removed (§12-24) while
    /// restoring ordinary visibility: with the block row gone, rule 4 no longer fires, and rule 2
    /// re-admits the removed voter through a still-Active mutual friend who extends access. The
    /// visibility gate lets the write through on that basis, and it is <c>Poll.Vote</c>'s own
    /// <c>RequireActiveParticipant</c> guard that must turn it away -- proving <c>poll_participant_removed</c>
    /// stays reachable over HTTP rather than silently regressing to a leaked 409 through
    /// <c>DomainExceptionMapper</c>'s generic fallback.
    /// </summary>
    [Fact]
    public async Task PollParticipantRemoved_VoteAfterBlockThenUnblock_Is404()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-code-removed-org-{Guid.NewGuid():N}@example.test");
        UserId removedVoterId = await _factory.SeedUserAsync(email: $"poll-code-removed-voter-{Guid.NewGuid():N}@example.test");
        UserId extenderId = await _factory.SeedUserAsync(email: $"poll-code-removed-extender-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, removedVoterId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, extenderId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, removedVoterId, extenderId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession removedVoterSession = await SessionEndpointTestSupport.SignInAsync(_factory, removedVoterId);
        (Guid pollId, Guid optionId) = await CreatePollWithFirstOptionAsync(
            organizerSession, [removedVoterId, extenderId]);

        // Block resolution goes by username through the registry table, which a freshly seeded user
        // never claims a row in (PartyUpApiFactory.SeedUserAsync, by design -- see its own remark) --
        // claim one here, mirroring SessionBlockScopeTests.EnsureDistinctUsernamesAsync's pattern.
        string removedVoterUsername = $"removedvoter{Guid.NewGuid():N}"[..20];
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User removedVoterUser = await context.DomainUsers.SingleAsync(u => u.Id == removedVoterId);
            Username username = Username.Create(removedVoterUsername);
            removedVoterUser.ChangeUsername(username, _factory.Clock);
            context.UsernameRegistry.Add(UsernameRegistryEntry.ClaimNew(username, removedVoterId));
            await context.SaveChangesAsync();
        }

        // Organizer blocks the voter -- the shipped block-effect scope marks their PollParticipant
        // Removed (§4 item 3) in the same transaction.
        HttpResponseMessage blockCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post, "/api/v1/blocks", JsonContent.Create(new { targetUsername = removedVoterUsername }));
        Assert.Equal(HttpStatusCode.Created, blockCreated.StatusCode);
        using JsonDocument blockBody = await SessionEndpointTestSupport.SuccessJsonAsync(blockCreated);
        Guid blockId = blockBody.RootElement.GetProperty("blockId").GetGuid();

        // Organizer unblocks -- the Block row is gone, but removal is permanent for this poll (§12-24).
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/blocks/{blockId:D}")).StatusCode);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await removedVoterSession.SendUnsafeAsync(
                HttpMethod.Put,
                $"/api/v1/polls/{pollId:D}/options/{optionId:D}/vote",
                JsonContent.Create(new { choice = "available" })),
            HttpStatusCode.NotFound,
            "poll_participant_removed");
    }

    /// <summary>
    /// Ruling A (ticket-P6-S4a-fix1): the WindowClosed collapse split into three distinct, honest
    /// outcomes across three separate polls (finalize is one-shot per poll).
    /// </summary>
    [Fact]
    public async Task FinalizeWindow_ProducesThreeDistinctOutcomes_RulingA()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-code-window-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        // Outcome 1: finalize attempted before the deadline on an option nobody has carried to the
        // minimum -- not yet a legal transition (D-187 opens this only for a viable option).
        (Guid earlyPollId, Guid earlyOptionId) =
            await CreatePollWithFirstOptionAsync(organizerSession, [], minAttendance: 2);
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{earlyPollId:D}/finalize",
                JsonContent.Create(new { optionId = earlyOptionId })),
            HttpStatusCode.Conflict,
            "poll_invalid_transition");

        // Outcome 2: finalize inside the legitimate window (Deadline <= now < Deadline+48h) succeeds.
        (Guid onTimePollId, Guid onTimeOptionId) = await CreatePollWithFirstOptionAsync(organizerSession, []);
        _factory.Clock.Advance(Duration.FromHours(25));
        HttpResponseMessage onTimeFinalize = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/polls/{onTimePollId:D}/finalize",
            JsonContent.Create(new { optionId = onTimeOptionId }));
        Assert.Equal(HttpStatusCode.OK, onTimeFinalize.StatusCode);

        // Outcome 3: finalize attempted from the 48-hour selection window's close onward.
        (Guid latePollId, Guid lateOptionId) = await CreatePollWithFirstOptionAsync(organizerSession, []);
        _factory.Clock.Advance(Duration.FromHours(73)); // 24h deadline + 48h window + margin
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{latePollId:D}/finalize",
                JsonContent.Create(new { optionId = lateOptionId })),
            HttpStatusCode.UnprocessableEntity,
            "poll_selection_window_closed");
    }

    /// <summary>Ruling A's cancel branch: the same design-named code once the selection window has closed.</summary>
    [Fact]
    public async Task CancelWindow_PastTheSelectionWindow_IsSelectionWindowClosed_RulingA()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-code-cancelwin-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, []);

        _factory.Clock.Advance(Duration.FromHours(73));

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/polls/{pollId:D}/cancel"),
            HttpStatusCode.UnprocessableEntity,
            "poll_selection_window_closed");
    }

    /// <summary>Ruling B / §12-12: finalize on a poll deleted down to zero options.</summary>
    [Fact]
    public async Task Finalize_OnAZeroOptionPoll_IsPollZeroOptions_RulingB()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-code-zero-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, []);

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        Guid[] optionIds = [.. detail.RootElement.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetGuid())];
        foreach (Guid optionId in optionIds)
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await organizerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/options/{optionId:D}")).StatusCode);
        }

        _factory.Clock.Advance(Duration.FromHours(25)); // past the deadline, still inside the 48h window

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/finalize",
                JsonContent.Create(new { optionId = Guid.NewGuid() })),
            HttpStatusCode.UnprocessableEntity,
            "poll_zero_options");
    }

    /// <summary>
    /// Reflects <see cref="PartyUpOpenApiTransformer"/>'s private problem-code registry, scoped to the
    /// Phase 6 poll section (from <c>poll_not_found</c> onward) -- <c>GameSessionEndpointTests</c>'s
    /// Phase 4 closure no longer includes this section (F1).
    /// </summary>
    private static string[] PollDocumentedProblemCodes()
    {
        FieldInfo field = typeof(PartyUpOpenApiTransformer).GetField(
            "ProblemCodes", BindingFlags.NonPublic | BindingFlags.Static)!;
        var allCodes = (string[])field.GetValue(null)!;
        int phase6Start = Array.IndexOf(allCodes, "poll_not_found");
        // PHASE7_DESIGN.md §4 (ticket-P7-S4): the S4 codes appended after the poll section are not
        // poll codes -- stop the slice there so this closure stays scoped to §4 the Phase 6 poll
        // surface, not swept up by a later phase's own appended codes.
        int phase7Start = Array.IndexOf(allCodes, "notification_not_found");
        return allCodes[phase6Start..phase7Start];
    }

    [Fact]
    public void EveryDocumentedPollProblemCodeIsExercisedOrAcceptedAsAResidual()
    {
        // Exercised directly by a [Fact] in this file.
        HashSet<string> exercisedInThisFile = new(StringComparer.Ordinal)
        {
            "poll_validation",
            "poll_option_cap",
            "poll_option_not_found",
            "poll_voting_closed",
            "poll_terminal",
            "poll_guest_not_found",
            "poll_conflict",
            "poll_invalid_transition", // FinalizeWindow_ProducesThreeDistinctOutcomes_RulingA outcome 1
            "poll_selection_window_closed", // FinalizeWindow_.../CancelWindow_...
            "poll_zero_options", // Finalize_OnAZeroOptionPoll_IsPollZeroOptions_RulingB
            "poll_participant_removed", // PollParticipantRemoved_VoteAfterBlockThenUnblock_Is404
        };

        // Exercised by named sibling tests elsewhere in the Polls folder.
        HashSet<string> exercisedBySiblingTests = new(StringComparer.Ordinal)
        {
            "poll_not_found", // PollEndpointTests + PollFailClosedTests (many)
            "poll_invites_closed", // PollInviteEndpointTests.InvitePollUsers_PastDeadlineIsPollInvitesClosed_D193
            // ticket-P6-S4b: PollGuestHttpTests exercises the poll guest arm's own code.
            "guest_link_not_claimed", // PollGuestHttpTests.VoteBeforeClaim_OverHttp_Is409_D017
        };

        HashSet<string> acceptedUntestedResiduals = new(StringComparer.Ordinal);

        HashSet<string> covered = new(exercisedInThisFile, StringComparer.Ordinal);
        covered.UnionWith(exercisedBySiblingTests);
        covered.UnionWith(acceptedUntestedResiduals);

        Assert.Equal(
            PollDocumentedProblemCodes().OrderBy(code => code, StringComparer.Ordinal),
            covered.OrderBy(code => code, StringComparer.Ordinal));
    }

    // ---- fixture helpers ------------------------------------------------------------------------

    private async Task<Guid> CreatePollAsync(SessionHttpSession organizerSession, IReadOnlyList<Instant> optionStartTimes)
    {
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        IReadOnlyList<Instant> times = optionStartTimes.Count >= 2
            ? optionStartTimes
            : [baseTime, baseTime + Duration.FromHours(1)];

        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Closure fixture poll",
                options = times.Select(time => new { startTime = SessionEndpointTestSupport.Iso(time) }).ToArray(),
                visibility = "private",
                minAttendance = 1,
                inviteeUserIds = Array.Empty<Guid>(),
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        return body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreatePollWithInviteesAsync(
        SessionHttpSession organizerSession, IReadOnlyList<UserId> inviteeIds, int minAttendance = 1)
    {
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Closure fixture poll",
                options = new[]
                {
                    new { startTime = SessionEndpointTestSupport.Iso(baseTime) },
                    new { startTime = SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(1)) },
                },
                visibility = inviteeIds.Count == 0 ? "private" : "public",
                minAttendance,
                inviteeUserIds = inviteeIds.Select(id => id.Value).ToArray(),
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        return body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
    }

    private async Task<(Guid PollId, Guid OptionId)> CreatePollWithFirstOptionAsync(
        SessionHttpSession organizerSession, IReadOnlyList<UserId> inviteeIds, int minAttendance = 1)
    {
        Guid pollId = await CreatePollWithInviteesAsync(organizerSession, inviteeIds, minAttendance);
        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        Guid optionId = detail.RootElement.GetProperty("options")[0].GetProperty("id").GetGuid();
        return (pollId, optionId);
    }
}
