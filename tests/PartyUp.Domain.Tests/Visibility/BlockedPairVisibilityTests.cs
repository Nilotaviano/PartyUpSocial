using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Sessions;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Visibility;

/// <summary>The §7 rule 4 walks: mutual invisibility, and the third-party carve-out.</summary>
public class BlockedPairVisibilityTests
{
    [Fact]
    public void Resolve_HidesTheOrganizersSessionFromTheirBlockedCounterpart_D065()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock);
        UserId counterpart = s.Invitee(0);
        Viewer blocked = TestViewers.Of(counterpart, blocked: [s.Organizer]);

        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(counterpart)).Grant);
        Assert.False(VisibilityEngine.ResolveSession(s.Session, blocked).IsVisible);
    }

    [Fact]
    public void Resolve_HidesTheSessionInBothDirectionsOfTheBlockedPair_D065()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest theirs = TestSessions.New(clock, inviteeCount: 1);
        UserId organizer = theirs.Organizer;
        UserId counterpart = theirs.Invitee(0);

        Session mine = Session.Create(counterpart, TestSessions.Details(), [organizer], clock);

        Assert.False(VisibilityEngine.ResolveSession(theirs.Session, TestViewers.Of(counterpart, blocked: [organizer])).IsVisible);
        Assert.False(VisibilityEngine.ResolveSession(mine, TestViewers.Of(organizer, blocked: [counterpart])).IsVisible);
    }

    [Fact]
    public void Resolve_OutranksEveryOtherRuleWhenTheOrganizerIsBlocked_D065()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        s.Session.OpenJoinRequest(requester, clock);

        // A Pending join request, an Origin = Invited row and an Accepted friendship with an
        // extender all grant access on their own; the block filter takes precedence over all three.
        Assert.False(VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(requester, blocked: [s.Organizer])).IsVisible);
        Assert.False(VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(s.Invitee(0), blocked: [s.Organizer])).IsVisible);
        Assert.False(VisibilityEngine
            .ResolveSession(s.Session, TestViewers.Of(UserId.New(), friends: [s.Invitee(0)], blocked: [s.Organizer]))
            .IsVisible);
    }

    [Fact]
    public void Resolve_KeepsBothBlockedUsersOnAThirdPartySession_D076()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 2);
        UserId first = s.Invitee(0);
        UserId second = s.Invitee(1);
        s.Session.Rsvp(s.InviteeRef(0), ParticipationStatus.Going, clock);
        s.Session.Rsvp(s.InviteeRef(1), ParticipationStatus.Going, clock);

        Assert.Equal(
            VisibilityGrant.Direct,
            VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(first, blocked: [second])).Grant);
        Assert.Equal(
            VisibilityGrant.Direct,
            VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(second, blocked: [first])).Grant);
    }

    [Fact]
    public void Resolve_LeavesAttendanceAndCapacityUntouchedForABlockedPair_D086()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 2, minAttendance: 3, maxCapacity: 3);
        s.Session.Rsvp(s.InviteeRef(0), ParticipationStatus.Going, clock);
        s.Session.Rsvp(s.InviteeRef(1), ParticipationStatus.Going, clock);

        Assert.Equal(3, s.Session.ActiveGoingCount);
        Assert.Equal(3, s.Session.ActiveOccupancy);
        Assert.Equal(SessionStatus.Confirmed, s.Session.Status);
        Assert.Equal(
            VisibilityGrant.Direct,
            VisibilityEngine.ResolveSession(s.Session, TestViewers.Of(s.Invitee(0), blocked: [s.Invitee(1)])).Grant);
    }

    [Fact]
    public void Resolve_NeverInheritsThroughABlockedCounterpart_D065()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);

        // Blocking deletes the friendship (§4), so this pairing cannot survive a block; the engine
        // still refuses the extender path rather than trusting the friend set it was handed.
        Viewer stale = TestViewers.Of(UserId.New(), friends: [s.Invitee(0)], blocked: [s.Invitee(0)]);

        Assert.False(VisibilityEngine.ResolveSession(s.Session, stale).IsVisible);
    }

    [Fact]
    public void Resolve_LosesTheExtenderPathOnceTheBlockExecutorRemovesTheParticipant_D077()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        Viewer thirdParty = TestViewers.FriendOf(s.Invitee(0));
        Assert.Equal(VisibilityGrant.Inherited, VisibilityEngine.ResolveSession(s.Session, thirdParty).Grant);

        s.Session.RemoveParticipation(s.InviteeRef(0), clock);

        Assert.Equal(ParticipationStatus.Removed, s.Row(0).Status);
        Assert.False(VisibilityEngine.ResolveSession(s.Session, thirdParty).IsVisible);
    }

    [Fact]
    public void Resolve_StillHidesTheSessionAfterTheBlockedRowIsRemoved_D077()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        s.Session.RemoveParticipation(s.InviteeRef(0), clock);

        Assert.False(VisibilityEngine
            .ResolveSession(s.Session, TestViewers.Of(s.Invitee(0), blocked: [s.Organizer]))
            .IsVisible);
    }
}
