using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Sessions;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Visibility;

public class SessionVisibilityTests
{
    // ---- rule 1: direct access ----------------------------------------------------------------

    [Fact]
    public void Resolve_GivesTheOrganizerDirectAccessWithoutAnyRelation_D019()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(s.Organizer));

        Assert.Equal(VisibilityGrant.Direct, result.Grant);
        Assert.Null(result.ExtenderId);
    }

    [Fact]
    public void Resolve_GivesAPendingInviteeDirectAccess_D018()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(s.Invitee(0)));

        Assert.Equal(VisibilityGrant.Direct, result.Grant);
    }

    [Fact]
    public void Resolve_KeepsADeclinedInviteeOnTheirInvitedOrigin_D019()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);
        s.Session.DeclineInvitation(s.InviteeRef(0), clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(s.Invitee(0)));

        Assert.Equal(VisibilityGrant.Direct, result.Grant);
    }

    [Fact]
    public void Resolve_DropsADepartedNonInvitee_D043()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        JoinRequest request = s.Session.OpenJoinRequest(requester, clock);
        s.Session.ApproveJoinRequest(request.Id, clock);
        s.Session.Leave(ParticipantRef.ForUser(requester), clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(requester));

        Assert.False(result.IsVisible);
    }

    [Fact]
    public void Resolve_KeepsAnApprovedRequesterWhoIsStillGoing_D043()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        JoinRequest request = s.Session.OpenJoinRequest(requester, clock);
        s.Session.ApproveJoinRequest(request.Id, clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(requester));

        Assert.Equal(VisibilityGrant.Direct, result.Grant);
    }

    [Fact]
    public void Resolve_GivesAPendingRequesterDirectAccess_D043()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        s.Session.OpenJoinRequest(requester, clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(requester));

        Assert.Equal(VisibilityGrant.Direct, result.Grant);
    }

    [Fact]
    public void Resolve_GrantsARejectedRequesterNothing_D043()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        JoinRequest request = s.Session.OpenJoinRequest(requester, clock);
        s.Session.RejectJoinRequest(request.Id, clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(requester));

        Assert.False(result.IsVisible);
    }

    [Fact]
    public void Resolve_GrantsAWithdrawnRequesterNothing_D043()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        JoinRequest request = s.Session.OpenJoinRequest(requester, clock);
        s.Session.WithdrawJoinRequest(request.Id, clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(requester));

        Assert.False(result.IsVisible);
    }

    [Fact]
    public void Resolve_GrantsALapsedRequesterNothing_D043()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        s.Session.OpenJoinRequest(requester, clock);
        s.Session.LapseJoinRequestsFrom(requester, clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(requester));

        Assert.False(result.IsVisible);
    }

    [Fact]
    public void Resolve_DropsARemovedRowDespiteItsInvitedOrigin_D077()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);
        s.Session.RemoveParticipation(s.InviteeRef(0), clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(s.Invitee(0)));

        Assert.Equal(ParticipationOrigin.Invited, s.Row(0).Origin);
        Assert.False(result.IsVisible);
    }

    // ---- privacy-flip retention matrix (§7 rule 3) ----------------------------------------------

    [Fact]
    public void PrivacyFlip_RetainsExactlyTheRuleOneHolders_D047()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 3);

        // Invited rows: one still Pending, one Going, one Declined — all Origin = Invited.
        s.Session.Rsvp(s.InviteeRef(1), ParticipationStatus.Going, clock);
        s.Session.DeclineInvitation(s.InviteeRef(2), clock);

        UserId departed = UserId.New();
        JoinRequest departedRequest = s.Session.OpenJoinRequest(departed, clock);
        s.Session.ApproveJoinRequest(departedRequest.Id, clock);
        s.Session.Leave(ParticipantRef.ForUser(departed), clock);

        UserId pendingRequester = UserId.New();
        s.Session.OpenJoinRequest(pendingRequester, clock);

        UserId rejectedRequester = UserId.New();
        JoinRequest rejected = s.Session.OpenJoinRequest(rejectedRequester, clock);
        s.Session.RejectJoinRequest(rejected.Id, clock);

        Viewer inheritedOnly = TestViewers.FriendOf(s.Organizer);
        Assert.Equal(VisibilityGrant.Inherited, VisibilityEngine.ResolveSession(s.Session, inheritedOnly).Grant);

        s.Session.ChangePrivacy(isPrivate: true, clock);

        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(s.Organizer)).Grant);
        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(s.Invitee(0))).Grant);
        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(s.Invitee(1))).Grant);
        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(s.Invitee(2))).Grant);
        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(pendingRequester)).Grant);

        Assert.False(VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(departed)).IsVisible);
        Assert.False(VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(rejectedRequester)).IsVisible);
        Assert.False(VisibilityEngine.ResolveSession(s.Session, inheritedOnly).IsVisible);
    }

    [Fact]
    public void Resolve_HidesAPrivateSessionFromAFriendOfAnExtender_D019()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, isPrivate: true);

        Assert.False(VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(s.Organizer)).IsVisible);
        Assert.False(VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(s.Invitee(0))).IsVisible);
    }

    [Fact]
    public void Resolve_RestoresInheritedAccessWhenTheSessionGoesPublicAgain_D047()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);
        Viewer friendOfOrganizer = TestViewers.FriendOf(s.Organizer);

        s.Session.ChangePrivacy(isPrivate: true, clock);
        Assert.False(VisibilityEngine.ResolveSession(s.Session, friendOfOrganizer).IsVisible);

        s.Session.ChangePrivacy(isPrivate: false, clock);
        Assert.Equal(VisibilityGrant.Inherited, VisibilityEngine.ResolveSession(s.Session, friendOfOrganizer).Grant);
    }

    // ---- rule 2: the D-018 extender matrix ------------------------------------------------------

    [Fact]
    public void Resolve_InheritsThroughAPendingInvitee_D018()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(s.Invitee(0)));

        Assert.Equal(VisibilityGrant.Inherited, result.Grant);
        Assert.Equal(s.Invitee(0), result.ExtenderId);
    }

    [Theory]
    [InlineData(ParticipationStatus.Going)]
    [InlineData(ParticipationStatus.Maybe)]
    public void Resolve_InheritsThroughAnActiveParticipant_D018(ParticipationStatus status)
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);
        s.Session.Rsvp(s.InviteeRef(0), status, clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(s.Invitee(0)));

        Assert.Equal(VisibilityGrant.Inherited, result.Grant);
    }

    [Theory]
    [InlineData(ParticipationStatus.Going)]
    [InlineData(ParticipationStatus.Maybe)]
    public void Resolve_InheritsThroughAWaitlistedParticipant_D018(ParticipationStatus status)
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1, maxCapacity: 1);
        s.Session.Rsvp(s.InviteeRef(0), status, clock);

        Assert.True(s.Row(0).IsWaitlisted);
        Assert.Equal(VisibilityGrant.Inherited, VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(s.Invitee(0))).Grant);
    }

    [Fact]
    public void Resolve_DoesNotInheritThroughADeclinedParticipant_D018()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);
        s.Session.DeclineInvitation(s.InviteeRef(0), clock);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(s.Invitee(0)));

        Assert.False(result.IsVisible);
    }

    [Fact]
    public void Resolve_DoesNotInheritThroughARemovedParticipant_D018()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);
        s.Session.RemoveParticipation(s.InviteeRef(0), clock);

        Assert.False(VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(s.Invitee(0))).IsVisible);
    }

    [Fact]
    public void Resolve_DoesNotInheritThroughARequesterOfAnyStatus_D018()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        UserId pending = UserId.New();
        s.Session.OpenJoinRequest(pending, clock);
        UserId rejectedRequester = UserId.New();
        JoinRequest rejected = s.Session.OpenJoinRequest(rejectedRequester, clock);
        s.Session.RejectJoinRequest(rejected.Id, clock);

        Assert.False(VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(pending)).IsVisible);
        Assert.False(VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(rejectedRequester)).IsVisible);
    }

    [Fact]
    public void Resolve_InheritsThroughARevivedPendingRowOfAnyOrigin_D018()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        JoinRequest request = s.Session.OpenJoinRequest(requester, clock);
        s.Session.ApproveJoinRequest(request.Id, clock);
        s.Session.Leave(ParticipantRef.ForUser(requester), clock);

        Viewer friendOfRequester = TestViewers.FriendOf(requester);
        Assert.False(VisibilityEngine.ResolveSession(s.Session, friendOfRequester).IsVisible);

        s.Session.ChangeStartTime(TestSessions.Origin + Duration.FromHours(48), clock);

        Participation revived = s.Row(ParticipantRef.ForUser(requester));
        Assert.Equal(ParticipationStatus.Pending, revived.Status);
        Assert.Equal(ParticipationOrigin.JoinRequest, revived.Origin);
        Assert.Equal(VisibilityGrant.Inherited, VisibilityEngine.ResolveSession(s.Session, friendOfRequester).Grant);
    }

    [Fact]
    public void Resolve_KeepsTheOrganizerAsAnExtenderWhileDeclined_D085()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);
        s.Session.Leave(s.OrganizerRef, clock);

        Assert.Equal(ParticipationStatus.Declined, s.Row(s.OrganizerRef).Status);
        Assert.False(SessionVisibility.ExtendsAccess(s.Row(s.OrganizerRef)));

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(s.Organizer));

        Assert.Equal(VisibilityGrant.Inherited, result.Grant);
        Assert.Equal(s.Organizer, result.ExtenderId);
    }

    [Fact]
    public void Resolve_KeepsTheOrganizerAsAnExtenderWhileMaybe_D085()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);
        s.Session.Rsvp(s.OrganizerRef, ParticipationStatus.Maybe, clock);

        Assert.Equal(VisibilityGrant.Inherited, VisibilityEngine.ResolveSession(s.Session, TestViewers.FriendOf(s.Organizer)).Grant);
    }

    [Fact]
    public void Resolve_GivesAStrangerNothingOnANonPrivateSession_D018()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);

        Assert.False(VisibilityEngine.ResolveSession(s.Session, TestViewers.Stranger()).IsVisible);
    }

    // ---- projection and fail-closed gate --------------------------------------------------------

    [Fact]
    public void Describe_LeavesGuestRowsOutOfTheExtenderSet_D018()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        (GuestLink link, _) = s.Session.CreateGuestLink(clock);
        s.Session.ClaimGuestLink(link.Id, DisplayName.Create("Casey"), ParticipationStatus.Going, clock);

        VisibilityTarget target = SessionVisibility.Describe(s.Session);

        Assert.Equal(3, s.Session.Participations.Count);
        Assert.Equal(2, target.Members.Count);
        Assert.All(target.Members, member => Assert.NotEqual(Guid.Empty, member.UserId.Value));
    }

    [Fact]
    public void Describe_CarriesOnlyOutstandingJoinRequests_D043()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        UserId pending = UserId.New();
        s.Session.OpenJoinRequest(pending, clock);
        UserId rejectedRequester = UserId.New();
        JoinRequest rejected = s.Session.OpenJoinRequest(rejectedRequester, clock);
        s.Session.RejectJoinRequest(rejected.Id, clock);

        VisibilityTarget target = SessionVisibility.Describe(s.Session);

        Assert.Equal(pending, Assert.Single(target.PendingRequesterIds));
        Assert.True(target.HasPendingRequestFrom(pending));
        Assert.False(target.HasPendingRequestFrom(rejectedRequester));
    }

    [Fact]
    public void Require_ReportsAnInvisibleSessionAsNotFound_D019()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, isPrivate: true);

        DomainException error = Assert.Throws<DomainException>(
            () => VisibilityEngine.RequireSession(s.Session, TestViewers.Stranger()));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void Require_ReturnsTheGrantForAVisibleSession_D019()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);

        VisibilityResult result = VisibilityEngine.RequireSession(s.Session, TestViewers.Of(s.Organizer));

        Assert.Equal(VisibilityGrant.Direct, result.Grant);
    }

    [Fact]
    public void Resolve_RejectsNullArguments_D019()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);
        VisibilityTarget target = SessionVisibility.Describe(s.Session);

        Assert.Throws<ArgumentNullException>(() => VisibilityEngine.Resolve(null!, TestViewers.Stranger()));
        Assert.Throws<ArgumentNullException>(() => VisibilityEngine.Resolve(target, null!));
        Assert.Throws<ArgumentNullException>(() => SessionVisibility.Describe(null!));
        Assert.Throws<ArgumentNullException>(() => SessionVisibility.ExtendsAccess(null!));
        Assert.Throws<ArgumentNullException>(() => SessionVisibility.KeepsDirectAccess(null!));
    }
}
