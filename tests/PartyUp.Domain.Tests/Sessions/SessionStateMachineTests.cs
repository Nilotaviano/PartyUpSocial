using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

public class SessionStateMachineTests
{
    [Fact]
    public void Confirm_WhenMinimumGoingReached_SetsConfirmed_D053()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2);
        fixture.Drain();

        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        Assert.Single(fixture.Drain().OfType<SessionConfirmed>());
    }

    [Fact]
    public void Confirm_CountsOnlyActiveGoing_D062()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2);

        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock);

        Assert.Equal(1, fixture.Session.ActiveGoingCount);
        Assert.Equal(2, fixture.Session.ActiveOccupancy);
        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
    }

    [Fact]
    public void Confirm_RemainsPossibleWhileTheSessionIsInProgress_D106()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2);

        clock.Reset(fixture.Session.StartTime + Duration.FromMinutes(30));
        fixture.Session.ApplyDueTransitions(clock);
        fixture.Drain();

        // A Pending row cannot respond after the start (D-081/D-089), but a join approval can (D-173).
        JoinRequest request = fixture.Session.OpenJoinRequest(UserId.New(), clock);
        fixture.Session.ApproveJoinRequest(request.Id, clock);

        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        Assert.Single(fixture.Drain().OfType<SessionConfirmed>());
    }

    [Fact]
    public void Confirm_IsRejectedOnceTheEndTimeHasPassed_D106()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2);
        Session session = fixture.Session;

        FakeClock ended = TestSessions.At(session.EndTime);

        DomainException error = Assert.Throws<DomainException>(() => session.Confirm(ended));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void Reevaluation_NeverUnconfirmsAStartedSession_D106()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        Session session = fixture.Session;
        Assert.Equal(SessionStatus.Confirmed, session.Status);

        clock.Reset(session.StartTime + Duration.FromMinutes(10));
        session.ApplyDueTransitions(clock);
        fixture.Drain();

        // The only Going row flips to Maybe in progress (D-089); the session still completes (D-106).
        session.Rsvp(fixture.OrganizerRef, ParticipationStatus.Maybe, clock);

        Assert.Equal(0, session.ActiveGoingCount);
        Assert.Equal(SessionStatus.Confirmed, session.Status);
        Assert.Empty(fixture.Drain().OfType<SessionUnconfirmed>());
    }

    [Fact]
    public void Unconfirm_RequiresGoingBelowTheMinimum_D053()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Session.Unconfirm(clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void Unconfirm_OnAStartedConfirmedSession_IsRejected_D106()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        Session session = fixture.Session;

        clock.Reset(session.StartTime + Duration.FromMinutes(1));
        session.ApplyDueTransitions(clock);

        DomainException error = Assert.Throws<DomainException>(() => session.Unconfirm(clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
        Assert.Equal(SessionStatus.Confirmed, session.Status);
    }

    [Fact]
    public void Complete_HappensAfterTheEndTime_D056()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        Session session = fixture.Session;

        clock.Reset(session.EndTime);
        session.ApplyDueTransitions(clock);

        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.True(session.IsRetainedInHistory);
    }

    [Fact]
    public void Complete_IsRejectedBeforeTheEndTime_D056()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Session.Complete(clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void Expire_TakesAnUnconfirmedPastSessionOutOfHistory_D164()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2);
        Session session = fixture.Session;

        clock.Reset(session.EndTime);
        session.ApplyDueTransitions(clock);

        Assert.Equal(SessionStatus.Expired, session.Status);
        Assert.False(session.IsRetainedInHistory);
    }

    [Fact]
    public void ApplyDueTransitions_IsIdempotent_D056()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        Session session = fixture.Session;

        clock.Reset(session.EndTime + Duration.FromDays(3));
        session.ApplyDueTransitions(clock);
        session.ApplyDueTransitions(clock);
        session.Complete(clock);

        Assert.Equal(SessionStatus.Completed, session.Status);
    }

    [Fact]
    public void ApplyDueTransitions_DoesNothingBeforeTheEndTime_D056()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        Session session = fixture.Session;

        clock.Reset(session.EndTime - Duration.FromMinutes(1));
        session.ApplyDueTransitions(clock);

        Assert.Equal(SessionStatus.Confirmed, session.Status);
    }

    [Fact]
    public void Cancel_OfALogicallyExpiredSession_IsImpossible_D164()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2);
        Session session = fixture.Session;

        // The sweeper never ran; the clock alone decides. Expire applies first (§1).
        clock.Reset(session.EndTime + Duration.FromHours(6));

        DomainException error = Assert.Throws<DomainException>(() => session.Cancel(clock));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
        Assert.Equal(SessionStatus.Expired, session.Status);
    }

    [Fact]
    public void Cancel_OfALogicallyCompletedSession_CancelsAsCompleted_D156()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        Session session = fixture.Session;

        clock.Reset(session.EndTime + Duration.FromHours(6));
        session.Cancel(clock);

        Assert.Equal(SessionStatus.Canceled, session.Status);
        Assert.False(session.IsRetainedInHistory);
    }

    [Fact]
    public void Cancel_OfAnAlreadyCompletedSession_IsAllowed_D157()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        Session session = fixture.Session;

        clock.Reset(session.EndTime);
        session.ApplyDueTransitions(clock);
        Assert.Equal(SessionStatus.Completed, session.Status);

        session.Cancel(clock);

        Assert.Equal(SessionStatus.Canceled, session.Status);
    }

    [Fact]
    public void Cancel_IsIdempotent_D156()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        fixture.Session.Cancel(clock);
        fixture.Drain();

        fixture.Session.Cancel(clock);

        Assert.Equal(SessionStatus.Canceled, fixture.Session.Status);
        Assert.Empty(fixture.Drain().OfType<SessionCanceled>());
    }

    [Fact]
    public void Cancel_NotifiesThroughTheOrganizerScopedEvent_D166()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        fixture.Drain();

        fixture.Session.Cancel(clock);

        SessionCanceled raised = Assert.Single(fixture.Drain().OfType<SessionCanceled>());
        Assert.Equal(fixture.Session.Id, raised.SessionId);
        Assert.Equal(fixture.Organizer, raised.OrganizerId);
    }

    [Fact]
    public void Cancel_RevokesEveryGuestLink_D174()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        (GuestLink unclaimed, _) = fixture.Session.CreateGuestLink(clock);
        (GuestLink claimed, _) = fixture.Session.CreateGuestLink(clock);
        fixture.Session.ClaimGuestLink(claimed.Id, DisplayName.Create("Sam"), ParticipationStatus.Going, clock);

        fixture.Session.Cancel(clock);

        Assert.Equal(GuestLinkState.RevokedByCancel, unclaimed.State);
        Assert.Equal(GuestLinkState.RevokedByCancel, claimed.State);
    }

    [Fact]
    public void TerminalStates_LapseEveryPendingJoinRequest_D173()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        JoinRequest request = fixture.Session.OpenJoinRequest(UserId.New(), clock);

        clock.Reset(fixture.Session.EndTime);
        fixture.Session.ApplyDueTransitions(clock);

        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
    }

    [Fact]
    public void Cancel_LapsesEveryPendingJoinRequest_D166()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        JoinRequest request = fixture.Session.OpenJoinRequest(UserId.New(), clock);

        fixture.Session.Cancel(clock);

        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
    }

    [Theory]
    [InlineData(SessionStatus.Canceled)]
    [InlineData(SessionStatus.Completed)]
    [InlineData(SessionStatus.Expired)]
    public void RosterCommands_AreRejectedInEveryTerminalState_D056(SessionStatus terminal)
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: terminal == SessionStatus.Expired ? 2 : 1);
        Session session = fixture.Session;

        if (terminal == SessionStatus.Canceled)
        {
            session.Cancel(clock);
        }
        else
        {
            clock.Reset(session.EndTime);
            session.ApplyDueTransitions(clock);
        }

        Assert.Equal(terminal, session.Status);

        DomainException error = Assert.Throws<DomainException>(
            () => session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock));

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void Transitions_RejectANullClock_D056()
    {
        FakeClock clock = TestSessions.Clock();
        Session session = TestSessions.New(clock).Session;

        Assert.Throws<ArgumentNullException>(() => session.ApplyDueTransitions(null!));
        Assert.Throws<ArgumentNullException>(() => session.Cancel(null!));
        Assert.Throws<ArgumentNullException>(() => session.Confirm(null!));
    }
}
