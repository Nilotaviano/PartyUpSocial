using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Application.Polls;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// ticket-P6-S4a-fix1 F2: the fail-closed test matrix the round-1 verifier enumerated as missing --
/// non-organizer probes on the organizer-only routes, and stranger/blocked probes on vote/suggest/
/// withdraw (the leak F2 fixes: <c>PollVoteUseCases</c>/<c>PollOptionUseCases.SuggestOptionAsync</c>/
/// <c>PollJoinRequestUseCases.WithdrawJoinRequestAsync</c> now resolve through
/// <see cref="PollActorVisibilityGate"/> before any domain call). Every probe below must equal the
/// missing-poll shape byte-for-byte: 404, code <c>poll_not_found</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollFailClosedTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task NonOrganizerProbes_OnEveryOrganizerOnlyRoute_ArePollNotFound()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-noo-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-noo-friend-{Guid.NewGuid():N}@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: $"poll-noo-req-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, requesterId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        using SessionHttpSession requesterSession = await SessionEndpointTestSupport.SignInAsync(_factory, requesterId);

        (Guid pollId, Guid optionId, Guid guestLinkId) = await CreatePollWithGuestLinkAsync(organizerSession, [friendId]);

        // A pending join request from `requesterId`, for the approve/reject probes.
        UserId outsideRequesterId = await _factory.SeedUserAsync(email: $"poll-noo-outside-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, outsideRequesterId);
        using SessionHttpSession outsideRequesterSession = await SessionEndpointTestSupport.SignInAsync(_factory, outsideRequesterId);
        HttpResponseMessage joinRequested = await outsideRequesterSession.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/polls/{pollId:D}/join-requests");
        using JsonDocument joinRequestBody = await SessionEndpointTestSupport.SuccessJsonAsync(joinRequested);
        Guid requestId = joinRequestBody.RootElement.GetProperty("id").GetGuid();

        // finalize
        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/polls/{pollId:D}/finalize", JsonContent.Create(new { optionId })),
            HttpStatusCode.NotFound, "poll_not_found");

        // mint guest links
        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/polls/{pollId:D}/guest-links", JsonContent.Create(new { count = 1 })),
            HttpStatusCode.NotFound, "poll_not_found");

        // revoke guest link
        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.SendUnsafeAsync(
                HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/guest-links/{guestLinkId:D}"),
            HttpStatusCode.NotFound, "poll_not_found");

        // remove guest
        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/polls/{pollId:D}/guests/{Guid.NewGuid():D}/remove"),
            HttpStatusCode.NotFound, "poll_not_found");

        // approve join request
        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/polls/{pollId:D}/join-requests/{requestId:D}/approve"),
            HttpStatusCode.NotFound, "poll_not_found");

        // reject join request
        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/polls/{pollId:D}/join-requests/{requestId:D}/reject"),
            HttpStatusCode.NotFound, "poll_not_found");
    }

    [Fact]
    public async Task StrangerProbes_OnVoteSuggestWithdraw_ArePollNotFound_F2()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-stranger-org-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"poll-stranger-{Guid.NewGuid():N}@example.test");

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);

        (Guid pollId, Guid optionId) = await CreatePollWithFirstOptionAsync(organizerSession, isPrivate: true);

        // Advance well past the deadline: pre-fix, this made a stranger's probe reveal
        // poll_voting_closed (422) instead of the missing-poll 404 (F2's core leak).
        _factory.Clock.Advance(Duration.FromHours(25));

        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.SendUnsafeAsync(
                HttpMethod.Put,
                $"/api/v1/polls/{pollId:D}/options/{optionId:D}/vote",
                JsonContent.Create(new { choice = "available" })),
            HttpStatusCode.NotFound, "poll_not_found");

        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/options",
                JsonContent.Create(new { startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(3)) })),
            HttpStatusCode.NotFound, "poll_not_found");

        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/join-requests/mine"),
            HttpStatusCode.NotFound, "poll_not_found");
    }

    [Fact]
    public async Task BlockedProbes_OnVoteSuggestWithdraw_ArePollNotFound_F2()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-blocked-org-{Guid.NewGuid():N}@example.test");
        UserId blockedId = await _factory.SeedUserAsync(email: $"poll-blocked-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, blockedId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        (Guid pollId, Guid optionId) = await CreatePollWithFirstOptionAsync(organizerSession, isPrivate: false, [blockedId]);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
            context.Blocks.Add(Block.Create(organizerId, blockedId, blockedUser.Username!.Value, _factory.Clock));
            await context.SaveChangesAsync();
        }

        using SessionHttpSession blockedSession = await SessionEndpointTestSupport.SignInAsync(_factory, blockedId);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await blockedSession.SendUnsafeAsync(
                HttpMethod.Put,
                $"/api/v1/polls/{pollId:D}/options/{optionId:D}/vote",
                JsonContent.Create(new { choice = "available" })),
            HttpStatusCode.NotFound, "poll_not_found");

        await SessionEndpointTestSupport.AssertProblemAsync(
            await blockedSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/options",
                JsonContent.Create(new { startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(3)) })),
            HttpStatusCode.NotFound, "poll_not_found");

        await SessionEndpointTestSupport.AssertProblemAsync(
            await blockedSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/join-requests/mine"),
            HttpStatusCode.NotFound, "poll_not_found");
    }

    // ---- fixture helpers ------------------------------------------------------------------------

    private async Task<(Guid PollId, Guid OptionId, Guid GuestLinkId)> CreatePollWithGuestLinkAsync(
        SessionHttpSession organizerSession, IReadOnlyList<UserId> inviteeIds)
    {
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Fail-closed fixture poll",
                optionStartTimes = new[] { SessionEndpointTestSupport.Iso(baseTime), SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(1)) },
                visibility = "public",
                minAttendance = 1,
                inviteeUserIds = inviteeIds.Select(id => id.Value).ToArray(),
                guestLinkCount = 1,
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
        Guid optionId = body.RootElement.GetProperty("poll").GetProperty("options")[0].GetProperty("id").GetGuid();
        Guid guestLinkId = body.RootElement.GetProperty("guestLinks")[0].GetProperty("guestLinkId").GetGuid();
        return (pollId, optionId, guestLinkId);
    }

    private async Task<(Guid PollId, Guid OptionId)> CreatePollWithFirstOptionAsync(
        SessionHttpSession organizerSession, bool isPrivate, IReadOnlyList<UserId>? inviteeIds = null)
    {
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Fail-closed fixture poll",
                optionStartTimes = new[] { SessionEndpointTestSupport.Iso(baseTime), SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(1)) },
                visibility = isPrivate ? "private" : "public",
                minAttendance = 1,
                inviteeUserIds = (inviteeIds ?? []).Select(id => id.Value).ToArray(),
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
        Guid optionId = body.RootElement.GetProperty("poll").GetProperty("options")[0].GetProperty("id").GetGuid();
        return (pollId, optionId);
    }
}
