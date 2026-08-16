using NodaTime.Testing;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Sessions;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Visibility;

public class GroupVisibilityTests
{
    // ---- rule 2g: group access ------------------------------------------------------------------

    [Fact]
    public void Resolve_GrantsAnAcceptedMemberOfATargetedGroupGroupAccess_D198()
    {
        FakeClock clock = TestSessions.Clock();
        FriendGroupId groupId = FriendGroupId.New();
        SessionUnderTest s = TestSessions.New(clock, visibility: ContentVisibility.Groups, audienceGroups: [groupId]);
        Viewer member = TestViewers.Of(UserId.New(), groupIds: [groupId]);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, member);

        Assert.Equal(VisibilityGrant.Group, result.Grant);
        Assert.Equal(groupId, result.GroupId);
    }

    [Fact]
    public void Resolve_DeniesAFriendOfAnExtenderOnAGroupsSession_D198()
    {
        FakeClock clock = TestSessions.Clock();
        FriendGroupId groupId = FriendGroupId.New();
        SessionUnderTest s = TestSessions.New(clock, visibility: ContentVisibility.Groups, audienceGroups: [groupId]);

        VisibilityResult result = VisibilityEngine.ResolveSession(
            s.Session,
            TestViewers.FriendOf(s.Organizer, s.Invitee(0)));

        Assert.Equal(VisibilityGrant.None, result.Grant);
        Assert.False(result.IsVisible);
    }

    [Fact]
    public void Resolve_DeniesAnInvitedButNotYetAcceptedGroupMember_D198()
    {
        FakeClock clock = TestSessions.Clock();
        FriendGroupId groupId = FriendGroupId.New();
        SessionUnderTest s = TestSessions.New(clock, visibility: ContentVisibility.Groups, audienceGroups: [groupId]);

        // A Viewer carries only owned and Accepted group ids, so an Invited-only membership is
        // modelled by its absence from the set.
        Viewer invitedOnly = TestViewers.Of(UserId.New(), groupIds: []);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, invitedOnly);

        Assert.Equal(VisibilityGrant.None, result.Grant);
    }

    [Fact]
    public void Resolve_KeepsAPendingJoinRequesterDirectOnAGroupsSession_D198()
    {
        FakeClock clock = TestSessions.Clock();
        FriendGroupId groupId = FriendGroupId.New();
        SessionUnderTest s = TestSessions.New(clock, visibility: ContentVisibility.Groups, audienceGroups: [groupId]);
        UserId requester = UserId.New();
        s.Session.OpenJoinRequest(requester, clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(requester));

        Assert.Equal(VisibilityGrant.Direct, result.Grant);
    }

    [Fact]
    public void Resolve_DeniesAGroupMemberBlockedWithTheOrganizer_D202()
    {
        FakeClock clock = TestSessions.Clock();
        FriendGroupId groupId = FriendGroupId.New();
        SessionUnderTest s = TestSessions.New(clock, visibility: ContentVisibility.Groups, audienceGroups: [groupId]);
        Viewer blockedMember = TestViewers.Of(UserId.New(), blocked: [s.Organizer], groupIds: [groupId]);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, blockedMember);

        Assert.Equal(VisibilityGrant.None, result.Grant);
    }

    [Fact]
    public void Resolve_DegradesAGroupsTargetWithNoAudienceToRuleOne_D205()
    {
        UserId organizer = UserId.New();
        UserId invitee = UserId.New();
        VisibilityTarget target = VisibilityTarget.Create(
            organizer,
            ContentVisibility.Groups,
            [],
            [VisibilityMember.Create(invitee, keepsDirectAccess: true, extendsAccess: true)],
            []);

        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.Resolve(target, TestViewers.Of(organizer)).Grant);
        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.Resolve(target, TestViewers.Of(invitee)).Grant);
        Assert.Equal(
            VisibilityGrant.None,
            VisibilityEngine.Resolve(target, TestViewers.Of(UserId.New(), groupIds: [FriendGroupId.New()])).Grant);
        Assert.Equal(VisibilityGrant.None, VisibilityEngine.Resolve(target, TestViewers.FriendOf(organizer)).Grant);
    }

    // ---- fail-closed on an out-of-range level -------------------------------------------------

    [Fact]
    public void Resolve_FailsClosedOnAnUnknownVisibilityLevel_D198()
    {
        UserId organizer = UserId.New();
        VisibilityTarget target = VisibilityTarget.Create(
            organizer,
            (ContentVisibility)7,
            [],
            [],
            []);

        VisibilityResult result = VisibilityEngine.Resolve(target, TestViewers.FriendOf(organizer));

        Assert.Equal(VisibilityGrant.None, result.Grant);
        Assert.False(result.IsVisible);
    }

    // ---- the two pre-existing levels, now enum-valued --------------------------------------------

    [Fact]
    public void Resolve_StillInheritsThroughAnExtenderOnAPublicSession_D018()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, visibility: ContentVisibility.Public);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(s.Invitee(0)));

        Assert.Equal(VisibilityGrant.Inherited, result.Grant);
        Assert.Equal(s.Invitee(0), result.ExtenderId);
        Assert.Null(result.GroupId);
    }

    [Fact]
    public void Resolve_StillDeniesAFriendOfTheOrganizerOnAPrivateSession_D043()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, visibility: ContentVisibility.Private);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(s.Organizer));

        Assert.Equal(VisibilityGrant.None, result.Grant);
    }

    [Fact]
    public void Resolve_GivesTheOrganizerDirectAccessAtEveryVisibilityLevel_D019()
    {
        FakeClock clock = TestSessions.Clock();
        FriendGroupId groupId = FriendGroupId.New();

        SessionUnderTest publicSession = TestSessions.New(clock, visibility: ContentVisibility.Public);
        SessionUnderTest privateSession = TestSessions.New(clock, visibility: ContentVisibility.Private);
        SessionUnderTest groupsSession = TestSessions.New(
            clock,
            visibility: ContentVisibility.Groups,
            audienceGroups: [groupId]);

        foreach (SessionUnderTest s in new[] { publicSession, privateSession, groupsSession })
        {
            VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(s.Organizer));

            Assert.Equal(VisibilityGrant.Direct, result.Grant);
            Assert.Null(result.GroupId);
        }
    }
}
