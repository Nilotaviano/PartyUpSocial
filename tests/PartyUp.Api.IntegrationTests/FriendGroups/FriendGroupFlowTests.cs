using System.Linq;
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

    /// <summary>
    /// D-206 regression pin for the list-vs-detail nondeterminism found via ledger T9: a viewer who is
    /// an Accepted member of two groups the session targets must resolve the exact same
    /// <c>viaGroup</c> whether the session is read off the list page or the detail GET. The audience
    /// is submitted group-B-then-group-A — the reverse of creation (and so of ascending id) order —
    /// specifically so the pin exercises the tie-break, not an accidental agreement between insertion
    /// order and the sort key. Before the audience-group load carried an explicit order, the two query
    /// shapes could materialize the owned <c>_audienceGroups</c> collection differently and hand
    /// <c>GroupProvenanceReader</c>'s "first match wins" tie-break a different candidate each time.
    /// The pin also nails the tie-break to ascending group id, not just cross-page agreement, so a
    /// regression that keeps the two pages consistent but reintroduces an unordered load elsewhere
    /// still fails.
    /// </summary>
    [Fact]
    public async Task ViaGroupTieBreakIsDeterministicAcrossListAndDetail_ViewerInTwoAudienceGroups_D206()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser viewer = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, viewer.Id);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);
        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewer.Id);

        Guid groupA = await CreateGroupAsync(ownerSession, "Tie-break group A");
        Guid groupB = await CreateGroupAsync(ownerSession, "Tie-break group B");
        await InviteMembersAsync(ownerSession, groupA, [viewer.Id.Value]);
        await AcceptInviteAsync(viewerSession, groupA);
        await InviteMembersAsync(ownerSession, groupB, [viewer.Id.Value]);
        await AcceptInviteAsync(viewerSession, groupB);

        HttpResponseMessage created = await ownerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Targeted at both groups",
                startTime = SessionEndpointTestSupport.Iso(
                    _factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                minAttendance = 1,
                visibility = "groups",
                // Reversed vs. creation order on purpose — see the method doc.
                audienceGroupIds = new[] { groupB, groupA },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid sessionId = createdBody.RootElement.GetProperty("session").GetProperty("id").GetGuid();

        Guid expectedViaGroup = groupA.CompareTo(groupB) < 0 ? groupA : groupB;

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        JsonElement detailViaGroup = detail.RootElement.GetProperty("viaGroup");
        Assert.Equal(JsonValueKind.Object, detailViaGroup.ValueKind);
        Assert.Equal(expectedViaGroup, detailViaGroup.GetProperty("id").GetGuid());

        using JsonDocument list = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync("/api/v1/sessions?scope=upcoming"));
        JsonElement listItem = list.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == sessionId);
        JsonElement listViaGroup = listItem.GetProperty("viaGroup");
        Assert.Equal(JsonValueKind.Object, listViaGroup.ValueKind);
        Assert.Equal(expectedViaGroup, listViaGroup.GetProperty("id").GetGuid());

        Assert.Equal(detailViaGroup.GetProperty("id").GetGuid(), listViaGroup.GetProperty("id").GetGuid());
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

    /// <summary>
    /// Closes the group-roster identity gap: the LIST card gets <c>memberCount</c> with no per-group
    /// detail fetch, and the DETAIL roster carries real nameplates for the owner and every Accepted
    /// member server-side — no client-side friends-list join required (D-199).
    /// </summary>
    [Fact]
    public async Task ListCarriesMemberCount_AndDetailCarriesNameplates_D199()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser member = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, member.Id);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);
        using SessionHttpSession memberSession = await SessionEndpointTestSupport.SignInAsync(_factory, member.Id);

        Guid groupId = await CreateGroupAsync(ownerSession, "Nameplate fixture group");
        await InviteMembersAsync(ownerSession, groupId, [member.Id.Value]);
        await AcceptInviteAsync(memberSession, groupId);

        // memberCount = owner (implicit) + 1 Accepted member = 2, off ONE list call, no detail fetch.
        using JsonDocument list = await SessionEndpointTestSupport.SuccessJsonAsync(
            await ownerSession.GetAsync("/api/v1/friend-groups"));
        JsonElement ownedCard = list.RootElement.GetProperty("owned").EnumerateArray()
            .Single(g => g.GetProperty("id").GetGuid() == groupId);
        Assert.Equal(2, ownedCard.GetProperty("memberCount").GetInt32());

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await ownerSession.GetAsync($"/api/v1/friend-groups/{groupId:D}"));
        Assert.Equal("HTTP Session Test", detail.RootElement.GetProperty("ownerDisplayName").GetString());
        Assert.False(detail.RootElement.GetProperty("ownerMasked").GetBoolean());

        JsonElement memberRow = detail.RootElement.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("userId").GetGuid() == member.Id.Value);
        Assert.Equal("HTTP Session Test", memberRow.GetProperty("displayName").GetString());
        Assert.False(memberRow.GetProperty("masked").GetBoolean());
        Assert.True(memberRow.TryGetProperty("avatar", out JsonElement avatar) && avatar.ValueKind == JsonValueKind.Object);
    }

    /// <summary>
    /// D-202: a block between two NON-OWNER members leaves both membership rows in place, masked at
    /// render time for the blocking viewer only — the other member's own read of the roster (and the
    /// owner's) is unaffected.
    /// </summary>
    [Fact]
    public async Task BlockBetweenTwoMembers_MasksTheCounterpartForTheBlockerOnly_D202()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser memberA = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser memberB = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, memberA.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, memberB.Id);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);
        using SessionHttpSession memberASession = await SessionEndpointTestSupport.SignInAsync(_factory, memberA.Id);
        using SessionHttpSession memberBSession = await SessionEndpointTestSupport.SignInAsync(_factory, memberB.Id);

        Guid groupId = await CreateGroupAsync(ownerSession, "Block-masking fixture group");
        await InviteMembersAsync(ownerSession, groupId, [memberA.Id.Value, memberB.Id.Value]);
        await AcceptInviteAsync(memberASession, groupId);
        await AcceptInviteAsync(memberBSession, groupId);

        HttpResponseMessage blockResponse = await memberASession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/blocks",
            JsonContent.Create(new { targetUsername = memberB.Username }));
        Assert.Equal(HttpStatusCode.Created, blockResponse.StatusCode);

        // memberA (the blocker) sees memberB masked, but the row is still listed (D-202: coexist).
        using JsonDocument aView = await SessionEndpointTestSupport.SuccessJsonAsync(
            await memberASession.GetAsync($"/api/v1/friend-groups/{groupId:D}"));
        JsonElement bRowForA = aView.RootElement.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("userId").GetGuid() == memberB.Id.Value);
        Assert.True(bRowForA.GetProperty("masked").GetBoolean());
        Assert.False(bRowForA.TryGetProperty("displayName", out _));
        Assert.False(bRowForA.TryGetProperty("avatar", out _));
        Assert.Equal("accepted", bRowForA.GetProperty("status").GetString());

        // The owner's own read is unaffected — the block is between two non-owner members.
        using JsonDocument ownerView = await SessionEndpointTestSupport.SuccessJsonAsync(
            await ownerSession.GetAsync($"/api/v1/friend-groups/{groupId:D}"));
        JsonElement bRowForOwner = ownerView.RootElement.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("userId").GetGuid() == memberB.Id.Value);
        Assert.False(bRowForOwner.GetProperty("masked").GetBoolean());
        Assert.Equal("HTTP Session Test", bRowForOwner.GetProperty("displayName").GetString());
    }

    /// <summary>
    /// D-210: the unfriend flow's one-shot read. Accepted rows in the caller's OWN groups only —
    /// an outstanding invite is not "remaining in" anything, someone else's group is not the
    /// caller's to surface, and a target sharing no owned group reads the same empty list a
    /// nonexistent user does.
    /// </summary>
    [Fact]
    public async Task ContainingUser_ReturnsOwnedAcceptedGroupsInOneCall_D210()
    {
        SessionHttpUser owner = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser member = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser bystander = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, owner.Id, member.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, bystander.Id, member.Id);

        using SessionHttpSession ownerSession = await SessionEndpointTestSupport.SignInAsync(_factory, owner.Id);
        using SessionHttpSession memberSession = await SessionEndpointTestSupport.SignInAsync(_factory, member.Id);
        using SessionHttpSession bystanderSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, bystander.Id);

        Guid acceptedGroupId = await CreateGroupAsync(ownerSession, "Accepted crew");
        await InviteMembersAsync(ownerSession, acceptedGroupId, [member.Id.Value]);
        await AcceptInviteAsync(memberSession, acceptedGroupId);

        Guid invitedOnlyGroupId = await CreateGroupAsync(ownerSession, "Invite pending crew");
        await InviteMembersAsync(ownerSession, invitedOnlyGroupId, [member.Id.Value]);

        Guid bystanderGroupId = await CreateGroupAsync(bystanderSession, "Not the owner's crew");
        await InviteMembersAsync(bystanderSession, bystanderGroupId, [member.Id.Value]);
        await AcceptInviteAsync(memberSession, bystanderGroupId);

        using JsonDocument ownerView = await SessionEndpointTestSupport.SuccessJsonAsync(
            await ownerSession.GetAsync($"/api/v1/friend-groups/containing/{member.Id.Value:D}"));
        JsonElement[] groups = [.. ownerView.RootElement.GetProperty("groups").EnumerateArray()];
        JsonElement only = Assert.Single(groups);
        Assert.Equal(acceptedGroupId, only.GetProperty("id").GetGuid());
        Assert.Equal("Accepted crew", only.GetProperty("name").GetString());
        Assert.Equal(2, only.GetProperty("memberCount").GetInt32());

        // A target the caller shares no owned group with, and a user id that names nobody, read alike.
        using JsonDocument noSharedGroups = await SessionEndpointTestSupport.SuccessJsonAsync(
            await ownerSession.GetAsync($"/api/v1/friend-groups/containing/{bystander.Id.Value:D}"));
        Assert.Empty(noSharedGroups.RootElement.GetProperty("groups").EnumerateArray());

        using JsonDocument unknownUser = await SessionEndpointTestSupport.SuccessJsonAsync(
            await ownerSession.GetAsync($"/api/v1/friend-groups/containing/{Guid.NewGuid():D}"));
        Assert.Empty(unknownUser.RootElement.GetProperty("groups").EnumerateArray());

        // The member's own read of the owner names nothing — the surface is the caller's owned groups.
        using JsonDocument memberView = await SessionEndpointTestSupport.SuccessJsonAsync(
            await memberSession.GetAsync($"/api/v1/friend-groups/containing/{owner.Id.Value:D}"));
        Assert.Empty(memberView.RootElement.GetProperty("groups").EnumerateArray());
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
