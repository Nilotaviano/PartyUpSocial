using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// D-193 over HTTP: <c>POST /api/v1/polls/{pollId}/invites</c>, mirroring
/// <c>GameSessionEndpointTests</c>' invite coverage of <c>POST /api/v1/sessions/{sessionId}/invites</c>
/// for the poll analogue — happy path, the fail-closed organizer-only 404, the 422
/// <c>poll_invites_closed</c> once the poll has left <c>Polling</c>, the 404 <c>user_not_found</c> for
/// a non-friend, and unauthenticated 401.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollInviteEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task InvitePollUsers_AddsAnActiveParticipantVisibleInPollDetail_D193()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-invite-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-invite-friend-{Guid.NewGuid():N}@example.test");
        UserId lateInviteeId = await _factory.SeedUserAsync(email: $"poll-invite-late-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, lateInviteeId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);

        HttpResponseMessage invited = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/polls/{pollId:D}/invites",
            JsonContent.Create(new { userIds = new[] { lateInviteeId.Value } }));

        Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        using JsonDocument invitedBody = await SessionEndpointTestSupport.SuccessJsonAsync(invited);
        Assert.Equal(pollId, invitedBody.RootElement.GetProperty("id").GetGuid());

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        JsonElement roster = detail.RootElement.GetProperty("roster");
        Assert.Contains(
            roster.EnumerateArray(),
            p => p.TryGetProperty("userId", out JsonElement userId) && userId.GetGuid() == lateInviteeId.Value);
    }

    [Fact]
    public async Task InvitePollUsers_NonOrganizerGetsPollNotFound_D007()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-invite-oo-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-invite-oo-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);

        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/invites",
                JsonContent.Create(new { userIds = new[] { UserId.New().Value } })),
            HttpStatusCode.NotFound,
            "poll_not_found");
    }

    [Fact]
    public async Task InvitePollUsers_PastDeadlineIsPollInvitesClosed_D193()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-invite-closed-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-invite-closed-friend-{Guid.NewGuid():N}@example.test");
        UserId lateInviteeId = await _factory.SeedUserAsync(email: $"poll-invite-closed-late-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, lateInviteeId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId], deadlineChoice: "hours6");

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/invites",
                JsonContent.Create(new { userIds = new[] { lateInviteeId.Value } })),
            HttpStatusCode.UnprocessableEntity,
            "poll_invites_closed");
    }

    [Fact]
    public async Task InvitePollUsers_NonFriendIsUserNotFound_D193_D020()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-invite-nf-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-invite-nf-friend-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"poll-invite-nf-stranger-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/invites",
                JsonContent.Create(new { userIds = new[] { strangerId.Value } })),
            HttpStatusCode.NotFound,
            "user_not_found");
    }

    [Fact]
    public async Task InvitePollUsers_RequiresAuthentication_D193()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-invite-auth-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-invite-auth-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);

        using HttpClient anonymous = _factory.CreateClient();
        HttpResponseMessage unauthenticated = await anonymous.PostAsJsonAsync(
            $"/api/v1/polls/{pollId:D}/invites",
            new { userIds = new[] { UserId.New().Value } });

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    private async Task<Guid> CreatePollAsync(
        SessionHttpSession organizerSession,
        IReadOnlyList<UserId> inviteeIds,
        string? deadlineChoice = null)
    {
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Invite poll fixture",
                optionStartTimes = new[]
                {
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(2)),
                },
                visibility = "public",
                minAttendance = 1,
                deadlineChoice,
                inviteeUserIds = inviteeIds.Select(id => id.Value).ToArray(),
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        return body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
    }
}
