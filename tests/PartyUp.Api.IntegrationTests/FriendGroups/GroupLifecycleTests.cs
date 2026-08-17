using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Notifications;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.FriendGroups;

/// <summary>
/// Block-effects, deletion, edit-narrowing, finalization-carry, and unfriend-survives-membership
/// coverage for FriendGroups (D-200/D-202/D-203/D-204/D-205), HTTP end to end over
/// <see cref="SessionEndpointTestSupport"/>'s plumbing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GroupLifecycleTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Blocking the owner↔member pair deletes the membership row, so the member loses the grant-based
    /// path into the OWNER's own Groups session (rule 4 also blocks it directly — the organizer is the
    /// blocker). But the member independently held a Going RSVP (rule 1) on a DIFFERENT Groups session,
    /// targeting the same group, organized by a third, unblocked party — that access is untouched: rule
    /// 1 does not care about group membership at all.
    /// </summary>
    [Fact]
    public async Task BlockSeversMembership_ButAGoingRsvpKeepsRule1AccessElsewhere_D202_D203()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser member = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser thirdOrganizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, member.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, thirdOrganizer.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, thirdOrganizer.Id, member.Id);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);
        using SessionHttpSession memberSession = await SessionEndpointTestSupport.SignInAsync(_factory, member.Id);
        using SessionHttpSession thirdSession = await SessionEndpointTestSupport.SignInAsync(_factory, thirdOrganizer.Id);

        Guid groupId = await CreateGroupAsync(ownerSession, "Block fixture group");
        await InviteMembersAsync(ownerSession, groupId, [member.Id.Value, thirdOrganizer.Id.Value]);
        await AcceptInviteAsync(memberSession, groupId);
        await AcceptInviteAsync(thirdSession, groupId);

        Guid ownerSessionId = await CreateGroupsSessionAsync(ownerSession, groupId, "Owner's group session", []);
        Guid thirdSessionId = await CreateGroupsSessionAsync(
            thirdSession, groupId, "Third party's group session", [member.Id.Value]);

        Assert.Equal(
            HttpStatusCode.OK,
            (await memberSession.SendUnsafeAsync(
                HttpMethod.Put,
                $"/api/v1/sessions/{thirdSessionId:D}/my-participation",
                JsonContent.Create(new { response = "Going" }))).StatusCode);

        HttpResponseMessage blockResponse = await ownerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/blocks",
            JsonContent.Create(new { targetUsername = member.Username }));
        Assert.Equal(HttpStatusCode.Created, blockResponse.StatusCode);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await memberSession.GetAsync($"/api/v1/sessions/{ownerSessionId:D}"),
            HttpStatusCode.NotFound,
            "session_not_found");

        using JsonDocument thirdView = await SessionEndpointTestSupport.SuccessJsonAsync(
            await memberSession.GetAsync($"/api/v1/sessions/{thirdSessionId:D}"));
        Assert.Equal("groups", thirdView.RootElement.GetProperty("visibility").GetString());
        // The member's group grant is gone (block deleted the membership row), so viaGroup no longer
        // resolves for them — but rule 1's Going participation still admits them.
        Assert.False(
            thirdView.RootElement.TryGetProperty("viaGroup", out JsonElement viaGroup)
            && viaGroup.ValueKind != JsonValueKind.Null);
    }

    /// <summary>
    /// D-205: deleting a targeted group notifies every OTHER affected organizer (the deleting owner
    /// already knows), and the session degrades to rule-1-only rather than 404ing for its organizer.
    /// </summary>
    [Fact]
    public async Task DeletingAGroup_NotifiesTheAffectedOrganizerAndTheSessionDegrades_D205()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser member = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, member.Id);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);
        using SessionHttpSession memberSession = await SessionEndpointTestSupport.SignInAsync(_factory, member.Id);

        Guid groupId = await CreateGroupAsync(ownerSession, "Deletion fixture group");
        await InviteMembersAsync(ownerSession, groupId, [member.Id.Value]);
        await AcceptInviteAsync(memberSession, groupId);

        // The member — an Accepted holder, not the owner — organizes their own session targeting it.
        Guid memberSessionId = await CreateGroupsSessionAsync(memberSession, groupId, "Member-organized", []);

        HttpResponseMessage deleted = await ownerSession.SendUnsafeAsync(
            HttpMethod.Delete, $"/api/v1/friend-groups/{groupId:D}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        bool notified = await context.Notifications.AnyAsync(n =>
            n.RecipientUserId == member.Id
            && n.Type == NotificationType.AudienceGroupDeleted);
        Assert.True(notified);

        // The owner who deleted their own group gets no notification about it (D-205's own note).
        bool ownerNotified = await context.Notifications.AnyAsync(n =>
            n.RecipientUserId == owner.Id
            && n.Type == NotificationType.AudienceGroupDeleted);
        Assert.False(ownerNotified);

        // Degenerate state: the session survives, rule-1-only, for its own organizer.
        using JsonDocument degraded = await SessionEndpointTestSupport.SuccessJsonAsync(
            await memberSession.GetAsync($"/api/v1/sessions/{memberSessionId:D}"));
        Assert.Equal("groups", degraded.RootElement.GetProperty("visibility").GetString());
        Assert.False(
            degraded.RootElement.TryGetProperty("viaGroup", out JsonElement viaGroup)
            && viaGroup.ValueKind != JsonValueKind.Null);
    }

    /// <summary>
    /// D-203: leaving the Groups level deletes the audience junction rows outright — flipping back to
    /// Groups later requires re-picking, never a silent resurrection of the old audience.
    /// </summary>
    [Fact]
    public async Task EditingFromGroupsToPublicDeletesJunctions_FlippingBackRequiresRePicking_D203()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser friend = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, friend.Id);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);

        Guid groupId = await CreateGroupAsync(ownerSession, "Edit-narrowing fixture group");
        Guid sessionId = await CreateGroupsSessionAsync(ownerSession, groupId, "Narrowing fixture", []);

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await ownerSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        string version = detail.RootElement.GetProperty("version").GetString()!;

        HttpResponseMessage toPublic = await ownerSession.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/sessions/{sessionId:D}",
            JsonContent.Create(new { visibility = "public", audienceGroupIds = Array.Empty<Guid>(), ifMatch = version }));
        using JsonDocument publicBody = await SessionEndpointTestSupport.SuccessJsonAsync(toPublic);
        Assert.Equal("public", publicBody.RootElement.GetProperty("visibility").GetString());
        string publicVersion = publicBody.RootElement.GetProperty("version").GetString()!;

        // Observe the wipe directly at the database boundary: the junction row the create call
        // inserted is gone, not merely unreachable through the HTTP surface.
        Assert.Equal(0, await JunctionRowCountAsync(sessionId));

        // Flipping back to Groups with an EMPTY audience must fail — the old targeting is gone, not
        // silently restored.
        await SessionEndpointTestSupport.AssertProblemAsync(
            await ownerSession.SendUnsafeAsync(
                HttpMethod.Patch,
                $"/api/v1/sessions/{sessionId:D}",
                JsonContent.Create(new { visibility = "groups", audienceGroupIds = Array.Empty<Guid>(), ifMatch = publicVersion })),
            HttpStatusCode.BadRequest,
            "session_validation");

        // Re-picking the same group explicitly succeeds.
        HttpResponseMessage backToGroups = await ownerSession.SendUnsafeAsync(
            HttpMethod.Patch,
            $"/api/v1/sessions/{sessionId:D}",
            JsonContent.Create(new { visibility = "groups", audienceGroupIds = new[] { groupId }, ifMatch = publicVersion }));
        using JsonDocument reGroupedBody = await SessionEndpointTestSupport.SuccessJsonAsync(backToGroups);
        Assert.Equal("groups", reGroupedBody.RootElement.GetProperty("visibility").GetString());
        Assert.Equal(1, await JunctionRowCountAsync(sessionId));
    }

    private async Task<int> JunctionRowCountAsync(Guid sessionId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Database
            .SqlQueryRaw<int>(
                """SELECT count(*)::int AS "Value" FROM session_audience_groups WHERE session_id = {0}""",
                sessionId)
            .SingleAsync();
    }

    /// <summary>
    /// D-204: finalization copies the poll's audience junction rows onto the minted session. Proven
    /// through a group member who is deliberately NOT a poll invitee and never votes — that member's
    /// only possible path into the minted session is a live rule-2g grant against the copied junction
    /// row, so this fails if the copy silently drops (unlike the poll's own invitee/voter, whose
    /// rule-1 involvement would mask a dropped audience carry).
    /// </summary>
    [Fact]
    public async Task PollFinalizationCarriesTheAudienceOntoTheMintedSession_D204()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser member = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser nonParticipantMember = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, member.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, nonParticipantMember.Id);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);
        using SessionHttpSession memberSession = await SessionEndpointTestSupport.SignInAsync(_factory, member.Id);
        using SessionHttpSession nonParticipantSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, nonParticipantMember.Id);

        Guid groupId = await CreateGroupAsync(ownerSession, "Finalization fixture group");
        await InviteMembersAsync(ownerSession, groupId, [member.Id.Value, nonParticipantMember.Id.Value]);
        await AcceptInviteAsync(memberSession, groupId);
        await AcceptInviteAsync(nonParticipantSession, groupId);

        HttpResponseMessage created = await ownerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Groups poll to finalize",
                optionStartTimes = new[]
                {
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(2)),
                },
                minAttendance = 1,
                visibility = "groups",
                audienceGroupIds = new[] { groupId },
                inviteeUserIds = new[] { member.Id.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument createBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = createBody.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
        Guid optionId = createBody.RootElement.GetProperty("poll").GetProperty("options")[0].GetProperty("id").GetGuid();

        HttpResponseMessage vote = await memberSession.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/polls/{pollId:D}/options/{optionId:D}/vote",
            JsonContent.Create(new { choice = "available" }));
        Assert.Equal(HttpStatusCode.OK, vote.StatusCode);

        _factory.Clock.Advance(Duration.FromHours(25));

        HttpResponseMessage finalize = await ownerSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/polls/{pollId:D}/finalize",
            JsonContent.Create(new { optionId }));
        using JsonDocument finalizeBody = await SessionEndpointTestSupport.SuccessJsonAsync(finalize);
        Guid sessionId = finalizeBody.RootElement.GetProperty("sessionId").GetGuid();

        using JsonDocument mintedForMember = await SessionEndpointTestSupport.SuccessJsonAsync(
            await memberSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        Assert.Equal("groups", mintedForMember.RootElement.GetProperty("visibility").GetString());

        // nonParticipantMember never voted and was never a poll invitee — the poll's own rule-1
        // involvement can't be masking anything for them. Their only route into the minted session is
        // a live rule-2g grant off the copied junction row.
        using JsonDocument mintedForNonParticipant = await SessionEndpointTestSupport.SuccessJsonAsync(
            await nonParticipantSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        Assert.Equal("groups", mintedForNonParticipant.RootElement.GetProperty("visibility").GetString());
        Assert.True(
            mintedForNonParticipant.RootElement.TryGetProperty("viaGroup", out JsonElement viaGroup)
            && viaGroup.ValueKind != JsonValueKind.Null);
        Assert.Equal(groupId, viaGroup.GetProperty("id").GetGuid());
    }

    /// <summary>D-200: friend-group membership is invite/accept consented and survives unfriending.</summary>
    [Fact]
    public async Task UnfriendingDoesNotRemoveGroupMembership_D200()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser member = await SessionEndpointTestSupport.SeedAsync(_factory);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);
        using SessionHttpSession memberSession = await SessionEndpointTestSupport.SignInAsync(_factory, member.Id);

        using JsonDocument pendingRequest = await SessionEndpointTestSupport.SuccessJsonAsync(
            await ownerSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/friend-requests",
                JsonContent.Create(new { targetUsername = member.Username })));
        Guid friendshipId = pendingRequest.RootElement.GetProperty("friendshipId").GetGuid();
        Assert.Equal(
            HttpStatusCode.OK,
            (await memberSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/friend-requests/{friendshipId:D}/accept")).StatusCode);

        Guid groupId = await CreateGroupAsync(ownerSession, "Unfriend fixture group");
        await InviteMembersAsync(ownerSession, groupId, [member.Id.Value]);
        await AcceptInviteAsync(memberSession, groupId);

        HttpResponseMessage unfriended = await ownerSession.SendUnsafeAsync(
            HttpMethod.Delete, $"/api/v1/friends/{friendshipId:D}");
        Assert.Equal(HttpStatusCode.NoContent, unfriended.StatusCode);

        // Membership survives: the member still reads the full roster (still Accepted), and the
        // owner's detail still lists them.
        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await memberSession.GetAsync($"/api/v1/friend-groups/{groupId:D}"));
        JsonElement members = detail.RootElement.GetProperty("members");
        Assert.Contains(
            members.EnumerateArray(),
            row => row.GetProperty("userId").GetGuid() == member.Id.Value
                && row.GetProperty("status").GetString() == "accepted");
    }

    // ---- shared HTTP fixtures ---------------------------------------------------------------------

    private static async Task<Guid> CreateGroupAsync(SessionHttpSession session, string name)
    {
        HttpResponseMessage created = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-groups",
            JsonContent.Create(new { name }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task InviteMembersAsync(SessionHttpSession ownerSession, Guid groupId, Guid[] userIds)
    {
        HttpResponseMessage response = await ownerSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/friend-groups/{groupId:D}/members",
            JsonContent.Create(new { userIds }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task AcceptInviteAsync(SessionHttpSession memberSession, Guid groupId)
    {
        HttpResponseMessage response = await memberSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/friend-groups/{groupId:D}/invitation/accept");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> CreateGroupsSessionAsync(
        SessionHttpSession organizerSession, Guid groupId, string title, Guid[] inviteeUserIds)
    {
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title,
                startTime = SessionEndpointTestSupport.Iso(
                    _factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                minAttendance = 1,
                visibility = "groups",
                audienceGroupIds = new[] { groupId },
                inviteeUserIds,
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        return body.RootElement.GetProperty("session").GetProperty("id").GetGuid();
    }
}
