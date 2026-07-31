using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

public class SessionRsvpTests
{
    [Fact]
    public void Rsvp_PendingToGoing_ConsumesCapacity_D084()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, maxCapacity: 4);

        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        Assert.Equal(2, fixture.Session.ActiveOccupancy);
        Assert.False(fixture.Row(0).IsWaitlisted);
    }

    [Fact]
    public void Rsvp_MaybeConsumesCapacityButNotTheMinimum_D062()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2, maxCapacity: 4);

        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock);

        Assert.Equal(2, fixture.Session.ActiveOccupancy);
        Assert.Equal(1, fixture.Session.ActiveGoingCount);
    }

    [Fact]
    public void Rsvp_AtCapacity_Waitlists_D084()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Drain();

        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);

        Assert.True(fixture.Row(1).IsWaitlisted);
        Assert.Equal(ParticipationStatus.Going, fixture.Row(1).Status);
        Assert.Equal(2, fixture.Session.ActiveOccupancy);
        Assert.Single(fixture.Drain().OfType<SessionWaitlistAdded>());
    }

    [Fact]
    public void Rsvp_AtCapacity_DoesNotDisplaceAnActiveMaybe_D084()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock);

        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);

        Assert.False(fixture.Row(0).IsWaitlisted);
        Assert.Equal(ParticipationStatus.Maybe, fixture.Row(0).Status);
        Assert.True(fixture.Row(1).IsWaitlisted);
    }

    [Fact]
    public void Rsvp_WithNoMaximum_NeverWaitlists_D091()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 5, minAttendance: 1, maxCapacity: null);

        for (int i = 0; i < 5; i++)
        {
            fixture.Session.Rsvp(fixture.InviteeRef(i), ParticipationStatus.Going, clock);
        }

        Assert.Equal(0, fixture.Session.WaitlistCount);
        Assert.Equal(6, fixture.Session.ActiveOccupancy);
        Assert.True(fixture.Session.HasFreeSlot);
    }

    [Fact]
    public void Rsvp_ActiveSelfFlipAtCapacity_NeverRetestsCapacity_D089()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Assert.False(fixture.Session.HasFreeSlot);

        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        Assert.False(fixture.Row(0).IsWaitlisted);
        Assert.Equal(ParticipationStatus.Going, fixture.Row(0).Status);
    }

    [Fact]
    public void Rsvp_WaitlistedSelfFlip_StaysWaitlisted_D089()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Assert.True(fixture.Row(0).IsWaitlisted);

        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock);

        Assert.True(fixture.Row(0).IsWaitlisted);
        Assert.Equal(ParticipationStatus.Maybe, fixture.Row(0).Status);
        Assert.Equal(1, fixture.Session.ActiveOccupancy);
    }

    [Fact]
    public void Rsvp_ByAPendingInvitee_ClosesAtTheSessionStart_D081()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);

        clock.Reset(fixture.Session.StartTime);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
        Assert.Equal(ParticipationStatus.Pending, fixture.Row(0).Status);
    }

    [Fact]
    public void Rsvp_PendingStaysPendingAtStart_D089()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);

        clock.Reset(fixture.Session.StartTime + Duration.FromMinutes(45));
        fixture.Session.ApplyDueTransitions(clock);

        Assert.Equal(ParticipationStatus.Pending, fixture.Row(0).Status);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
        Assert.Equal(ParticipationStatus.Pending, fixture.Row(0).Status);
    }

    [Fact]
    public void Rsvp_GoingMaybeFlipsStayOpenInProgress_D089()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        clock.Reset(fixture.Session.StartTime + Duration.FromMinutes(5));
        fixture.Session.ApplyDueTransitions(clock);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock);

        Assert.Equal(ParticipationStatus.Maybe, fixture.Row(0).Status);
    }

    [Fact]
    public void Rsvp_IsRejectedOnceTheSessionHasEnded_D173()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        clock.Reset(fixture.Session.EndTime);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock));

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void Rsvp_RejectsDeclinedAsAResponse_D055()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Declined, clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Rsvp_ByARemovedParticipant_IsRejected_D077()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        fixture.Session.RemoveParticipation(fixture.InviteeRef(0), clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock));

        Assert.Equal(DomainError.RemovalIsPermanent, error.Error);
    }

    [Fact]
    public void Rsvp_ByAStranger_IsNotFound_D081()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.Rsvp(ParticipantRef.ForUser(UserId.New()), ParticipationStatus.Going, clock));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void Rsvp_ByTheOrganizerAfterDecliningAtAFullSession_Waitlists_D084()
    {
        // §12-2: the organizer holds no capacity privilege outside D-119 finalization.
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 2, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);

        fixture.Session.Leave(fixture.OrganizerRef, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.OrganizerRef, ParticipationStatus.Going, clock);

        Participation organizer = fixture.Row(fixture.OrganizerRef);
        Assert.True(organizer.IsWaitlisted);
        Assert.Equal(ParticipationStatus.Going, organizer.Status);
    }

    [Fact]
    public void Leave_ByAGoingParticipant_FreesCapacity_D055()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 3);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        fixture.Session.Leave(fixture.InviteeRef(0), clock);

        Assert.Equal(ParticipationStatus.Declined, fixture.Row(0).Status);
        Assert.Equal(1, fixture.Session.ActiveOccupancy);
    }

    [Fact]
    public void Leave_ByAWaitlistedParticipant_IsAllowedBeforeStart_D055()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Assert.True(fixture.Row(0).IsWaitlisted);

        fixture.Session.Leave(fixture.InviteeRef(0), clock);

        Assert.Equal(ParticipationStatus.Declined, fixture.Row(0).Status);
        Assert.False(fixture.Row(0).IsWaitlisted);
    }

    [Fact]
    public void Leave_IsRejectedOnceTheSessionHasStarted_D089()
    {
        // §12-3: in-progress attendance changes are restricted to active Going↔Maybe.
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        clock.Reset(fixture.Session.StartTime);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.Leave(fixture.InviteeRef(0), clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void Leave_ByAPendingInvitee_IsRejected_D055()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.Leave(fixture.InviteeRef(0), clock));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }

    [Fact]
    public void DeclineInvitation_ClosesAtTheSessionStart_D081()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);

        clock.Reset(fixture.Session.StartTime);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.DeclineInvitation(fixture.InviteeRef(0), clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void DeclineInvitation_LeavesAPendingRowDeclined_D081()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        fixture.Session.DeclineInvitation(fixture.InviteeRef(0), clock);

        Assert.Equal(ParticipationStatus.Declined, fixture.Row(0).Status);
        Assert.Equal(ParticipationOrigin.Invited, fixture.Row(0).Origin);
    }

    [Fact]
    public void Leave_ByTheLastGoingParticipant_Unconfirms_D053()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        fixture.Drain();

        fixture.Session.Leave(fixture.InviteeRef(0), clock);

        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
        Assert.Single(fixture.Drain().OfType<SessionUnconfirmed>());
    }

    [Fact]
    public void SetReminderChoice_OverridesTheAccountDefault_D082()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        fixture.Session.SetReminderChoice(fixture.Invitee(0), ReminderOffset.Min30, clock);

        Assert.Equal(ReminderOffset.Min30, fixture.Row(0).ReminderChoice);

        fixture.Session.SetReminderChoice(fixture.Invitee(0), null, clock);
        Assert.Null(fixture.Row(0).ReminderChoice);
    }

    [Fact]
    public void Invite_AfterCreation_AddsAPendingRow_D020()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        fixture.Drain();
        UserId late = UserId.New();

        Participation row = fixture.Session.Invite(late, clock);

        Assert.Equal(ParticipationStatus.Pending, row.Status);
        Assert.Equal(ParticipationOrigin.Invited, row.Origin);
        Assert.Equal(1, fixture.Session.ActiveOccupancy);
        SessionInvitationSent raised = Assert.Single(fixture.Drain().OfType<SessionInvitationSent>());
        Assert.Equal(late, raised.InviteeId);
    }

    [Fact]
    public void Invite_RevivesADeclinedRowAsPending_D020()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        fixture.Session.DeclineInvitation(fixture.InviteeRef(0), clock);

        Participation row = fixture.Session.Invite(fixture.Invitee(0), clock);

        Assert.Equal(ParticipationStatus.Pending, row.Status);
        Assert.Equal(ParticipationOrigin.Invited, row.Origin);
    }

    [Fact]
    public void Invite_NeverRevivesARemovedRow_D077()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        fixture.Session.RemoveParticipation(fixture.InviteeRef(0), clock);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.Invite(fixture.Invitee(0), clock);
            });

        Assert.Equal(DomainError.RemovalIsPermanent, error.Error);
    }

    [Fact]
    public void Invite_RejectsABlockedCounterpart_D065()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        UserId blocked = UserId.New();

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.Invite(blocked, clock, counterpart => counterpart == blocked);
            });

        Assert.Equal(DomainError.Blocked, error.Error);
        Assert.Null(fixture.Session.Find(ParticipantRef.ForUser(blocked)));
    }

    [Fact]
    public void Invite_ClosesAtTheSessionStart_D081()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);

        clock.Reset(fixture.Session.StartTime);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.Invite(UserId.New(), clock);
            });

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void Invite_RejectsSomeoneAlreadyOnTheRoster_D020()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.Invite(fixture.Invitee(0), clock);
            });

        Assert.Equal(DomainError.Duplicate, error.Error);
    }

    [Fact]
    public void Invite_RejectsTheOrganizer_D075()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.Invite(fixture.Organizer, clock);
            });

        Assert.Equal(DomainError.Conflict, error.Error);
    }
}
