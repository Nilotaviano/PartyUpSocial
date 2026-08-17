using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// D-186 over HTTP: <c>DELETE /api/v1/polls/{pollId}/my-participation</c>. The route is
/// authenticated, fails closed for anyone who cannot see the poll (a non-participant must not learn
/// it exists), and on success erases the caller's participant row and their votes from the real
/// store — the DB assertions are the point, since the deletion is what an over-broad or missing EF
/// removal would get wrong.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollLeaveHttpTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task LeavePoll_Withdraws204AndErasesTheRowAndVotes_D186()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-leave-org-{Guid.NewGuid():N}@example.test");
        UserId leaverId = await _factory.SeedUserAsync(email: $"poll-leave-voter-{Guid.NewGuid():N}@example.test");
        UserId stayerId = await _factory.SeedUserAsync(email: $"poll-leave-stayer-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, leaverId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, stayerId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [leaverId, stayerId]);
        (Guid firstOptionId, Guid secondOptionId) = await OptionIdsAsync(organizerSession, pollId);

        using SessionHttpSession leaverSession = await SessionEndpointTestSupport.SignInAsync(_factory, leaverId);
        using SessionHttpSession stayerSession = await SessionEndpointTestSupport.SignInAsync(_factory, stayerId);
        await VoteAsync(leaverSession, pollId, firstOptionId, "available");
        await VoteAsync(leaverSession, pollId, secondOptionId, "maybe");
        await VoteAsync(stayerSession, pollId, firstOptionId, "available");

        // The poll's creation invitations already exist; D-088 says the leave adds nothing to them.
        List<Guid> notificationsBefore = await PollNotificationIdsAsync(pollId);
        Assert.NotEmpty(notificationsBefore);

        HttpResponseMessage left = await leaverSession.SendUnsafeAsync(
            HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/my-participation");

        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PollId id = PollId.From(pollId);

            Assert.Empty(await context.PollParticipants
                .Where(p => p.PollId == id && p.UserId == leaverId)
                .ToListAsync());
            Assert.Empty(await context.Set<PollVote>()
                .Where(v => v.PollId == id && v.UserId == leaverId)
                .ToListAsync());

            // Nothing belonging to anyone else moved.
            Assert.Single(await context.Set<PollVote>()
                .Where(v => v.PollId == id && v.UserId == stayerId)
                .ToListAsync());
            Assert.Equal(
                2,
                await context.PollParticipants.CountAsync(p => p.PollId == id && p.Status == PollParticipantStatus.Active));
            Assert.Equal(2, await context.Set<PollOption>().CountAsync(o => o.PollId == id));
        }

        // The leaver no longer inherits visibility, so the poll is gone for them — fail-closed.
        await SessionEndpointTestSupport.AssertProblemAsync(
            await leaverSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/my-participation"),
            HttpStatusCode.NotFound,
            "poll_not_found");

        // The organizer sees only the roster shrink: leaving created no notification for anyone,
        // and removed none either (D-088).
        Assert.Equal(notificationsBefore, await PollNotificationIdsAsync(pollId));
    }

    private async Task<List<Guid>> PollNotificationIdsAsync(Guid pollId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<Notification> rows = await context.Notifications
            .Where(n => n.PollId == PollId.From(pollId))
            .ToListAsync();
        return [.. rows.Select(n => n.Id.Value).OrderBy(id => id)];
    }

    [Fact]
    public async Task LeavePoll_RequiresAuthenticationAndHidesPollsTheCallerCannotSee_D186_D114()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-leave-fc-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-leave-fc-friend-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"poll-leave-fc-str-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);

        using HttpClient anonymous = _factory.CreateClient();
        HttpResponseMessage unauthenticated = await anonymous.DeleteAsync(
            $"/api/v1/polls/{pollId:D}/my-participation");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        // A stranger gets the same answer for this poll as for one that does not exist.
        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);
        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/my-participation"),
            HttpStatusCode.NotFound,
            "poll_not_found");
        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{Guid.NewGuid():D}/my-participation"),
            HttpStatusCode.NotFound,
            "poll_not_found");

        // The organizer holds the poll's own row and cannot leave it.
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/my-participation"),
            HttpStatusCode.Conflict,
            "poll_conflict");

        // A canceled poll is terminal: the late leave mutates nothing.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/polls/{pollId:D}/cancel")).StatusCode);

        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/my-participation"),
            HttpStatusCode.Conflict,
            "poll_terminal");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await context.PollParticipants
            .Where(p => p.PollId == PollId.From(pollId) && p.UserId == friendId)
            .ToListAsync());
    }

    private async Task<Guid> CreatePollAsync(SessionHttpSession organizerSession, IReadOnlyList<UserId> inviteeIds)
    {
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Leave poll fixture",
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

    private static async Task<(Guid First, Guid Second)> OptionIdsAsync(SessionHttpSession session, Guid pollId)
    {
        using JsonDocument view = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync($"/api/v1/polls/{pollId:D}"));
        JsonElement options = view.RootElement.GetProperty("options");
        return (options[0].GetProperty("id").GetGuid(), options[1].GetProperty("id").GetGuid());
    }

    private static async Task VoteAsync(SessionHttpSession session, Guid pollId, Guid optionId, string choice)
    {
        HttpResponseMessage voted = await session.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/polls/{pollId:D}/options/{optionId:D}/vote",
            JsonContent.Create(new { choice }));
        Assert.Equal(HttpStatusCode.OK, voted.StatusCode);
    }
}
