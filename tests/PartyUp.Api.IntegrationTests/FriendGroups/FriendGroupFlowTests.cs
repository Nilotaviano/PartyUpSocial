using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;

namespace PartyUp.Api.IntegrationTests.FriendGroups;

/// <summary>
/// The FriendGroup HTTP surface end to end (D-198/D-199/D-202/D-206/D-208), reusing
/// <see cref="SessionEndpointTestSupport"/>'s sign-in/antiforgery plumbing the same way
/// <c>PollFailClosedTests</c> does.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FriendGroupFlowTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task CreateInviteAcceptTargetSession_MemberSeesAndCanRequestToJoin_D198()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser member = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, member.Id);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);
        using SessionHttpSession memberSession = await SessionEndpointTestSupport.SignInAsync(_factory, member.Id);

        Guid groupId = await CreateGroupAsync(ownerSession, "D&D crew");
        await InviteMembersAsync(ownerSession, groupId, [member.Id.Value]);
        await AcceptInviteAsync(memberSession, groupId);

        HttpResponseMessage created = await ownerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Targeted at the group",
                startTime = SessionEndpointTestSupport.Iso(
                    _factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                minAttendance = 1,
                visibility = "groups",
                audienceGroupIds = new[] { groupId },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid sessionId = createdBody.RootElement.GetProperty("session").GetProperty("id").GetGuid();

        using JsonDocument memberView = await SessionEndpointTestSupport.SuccessJsonAsync(
            await memberSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        Assert.Equal("groups", memberView.RootElement.GetProperty("visibility").GetString());

        HttpResponseMessage joinRequested = await memberSession.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/sessions/{sessionId:D}/join-requests");
        Assert.Equal(HttpStatusCode.Created, joinRequested.StatusCode);
    }

    /// <summary>
    /// D-206 regression pin: <c>CreateSessionResponse.Session</c> resolves through the exact same
    /// <c>GetSessionDetailAsync</c> path as the follow-up GET (both share
    /// <c>ResolveProvenanceAsync</c>'s membership-based, not grant-based, lookup) — a member who is
    /// also the session's own organizer must see the identical <c>viaGroup</c> in both responses.
    /// </summary>
    [Fact]
    public async Task CreateResponseViaGroupMatchesFollowUpGet_ForAMemberOrganizer_D206()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser member = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, member.Id);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);
        using SessionHttpSession memberSession = await SessionEndpointTestSupport.SignInAsync(_factory, member.Id);

        Guid groupId = await CreateGroupAsync(ownerSession, "Pin fixture group");
        await InviteMembersAsync(ownerSession, groupId, [member.Id.Value]);
        await AcceptInviteAsync(memberSession, groupId);

        // The member — an Accepted group holder — organizes their own Groups session targeting it.
        HttpResponseMessage created = await memberSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Member-organized, group-targeted",
                startTime = SessionEndpointTestSupport.Iso(
                    _factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                minAttendance = 1,
                visibility = "groups",
                audienceGroupIds = new[] { groupId },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        JsonElement createdSession = createdBody.RootElement.GetProperty("session");
        Guid sessionId = createdSession.GetProperty("id").GetGuid();

        using JsonDocument followUp = await SessionEndpointTestSupport.SuccessJsonAsync(
            await memberSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        JsonElement followUpSession = followUp.RootElement;

        bool createdHasViaGroup = createdSession.TryGetProperty("viaGroup", out JsonElement createdViaGroup)
            && createdViaGroup.ValueKind != JsonValueKind.Null;
        bool followUpHasViaGroup = followUpSession.TryGetProperty("viaGroup", out JsonElement followUpViaGroup)
            && followUpViaGroup.ValueKind != JsonValueKind.Null;

        Assert.True(createdHasViaGroup);
        Assert.True(followUpHasViaGroup);
        Assert.Equal(followUpViaGroup.GetProperty("id").GetGuid(), createdViaGroup.GetProperty("id").GetGuid());
        Assert.Equal(followUpViaGroup.GetProperty("name").GetString(), createdViaGroup.GetProperty("name").GetString());
    }

    [Fact]
    public async Task NonMemberGetIsIdenticalToNonexistent_D199()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser stranger = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);
        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, stranger.Id);

        Guid groupId = await CreateGroupAsync(ownerSession, "Hidden from strangers");

        HttpResponseMessage hidden = await strangerSession.GetAsync($"/api/v1/friend-groups/{groupId:D}");
        HttpResponseMessage missing = await strangerSession.GetAsync($"/api/v1/friend-groups/{Guid.NewGuid():D}");

        await SessionEndpointTestSupport.AssertProblemAsync(hidden, HttpStatusCode.NotFound, "group_not_found");
        await SessionEndpointTestSupport.AssertProblemAsync(missing, HttpStatusCode.NotFound, "group_not_found");
    }

    [Fact]
    public async Task InvitingABlockedFriend_FailsWithTheSameCodeAsAnUnknownUser_D202()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser blockedFriend = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, blockedFriend.Id);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);

        HttpResponseMessage blockResponse = await ownerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/blocks",
            JsonContent.Create(new { targetUsername = blockedFriend.Username }));
        Assert.Equal(HttpStatusCode.Created, blockResponse.StatusCode);

        Guid groupId = await CreateGroupAsync(ownerSession, "Blocked invite fixture");

        HttpResponseMessage blockedInvite = await ownerSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/friend-groups/{groupId:D}/members",
            JsonContent.Create(new { userIds = new[] { blockedFriend.Id.Value } }));

        HttpResponseMessage strangerInvite = await ownerSession.SendUnsafeAsync(
            HttpMethod.Post,
            $"/api/v1/friend-groups/{groupId:D}/members",
            JsonContent.Create(new { userIds = new[] { Guid.NewGuid() } }));

        // FriendGroup.Invite throws the domain-level DomainError.NotFound directly ("not_found" on
        // the wire, via the generic DomainExceptionMapper) — deliberately identical for a blocked
        // counterpart and a genuine stranger, D-202's no-block-detection-oracle rule.
        await SessionEndpointTestSupport.AssertProblemAsync(blockedInvite, HttpStatusCode.NotFound, "not_found");
        await SessionEndpointTestSupport.AssertProblemAsync(strangerInvite, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task A51stOwnedGroup_Is409GroupLimitReached_D208()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);

        for (int i = 0; i < 50; i++)
        {
            await CreateGroupAsync(ownerSession, $"Cap fixture {i} {Guid.NewGuid():N}"[..40]);
        }

        HttpResponseMessage overCap = await ownerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-groups",
            JsonContent.Create(new { name = $"One too many {Guid.NewGuid():N}"[..40] }));

        await SessionEndpointTestSupport.AssertProblemAsync(
            overCap, HttpStatusCode.Conflict, "group_limit_reached");
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
}
