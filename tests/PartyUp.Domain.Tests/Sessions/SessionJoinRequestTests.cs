using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

public class SessionJoinRequestTests
{
    [Fact]
    public void OpenJoinRequest_NotifiesTheOrganizer_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        fixture.Drain();
        UserId requester = UserId.New();

        JoinRequest request = fixture.Session.OpenJoinRequest(requester, clock);

        Assert.Equal(JoinRequestStatus.Pending, request.Status);
        Assert.Equal(fixture.Session.Id, request.SessionId);
        Assert.Null(request.PollId);
        SessionJoinRequestReceived raised = Assert.Single(fixture.Drain().OfType<SessionJoinRequestReceived>());
        Assert.Equal(requester, raised.RequesterId);
    }

    [Fact]
    public void OpenJoinRequest_StaysOpenWhileTheSessionIsInProgress_D173()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);

        clock.Reset(fixture.Session.StartTime + Duration.FromMinutes(30));
        JoinRequest request = fixture.Session.OpenJoinRequest(UserId.New(), clock);

        Assert.Equal(JoinRequestStatus.Pending, request.Status);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
    }

    [Fact]
    public void OpenJoinRequest_ClosesAtTheSessionEndTime_D173()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);

        clock.Reset(fixture.Session.EndTime);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.OpenJoinRequest(UserId.New(), clock);
            });

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void OpenJoinRequest_ByARemovedParticipant_IsADomainError_D077()
    {
        // §5.3: removal is permanent for this target, and that must not surface as a
        // uniqueness-constraint crash at approval time.
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        fixture.Session.RemoveParticipation(fixture.InviteeRef(0), clock);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.OpenJoinRequest(fixture.Invitee(0), clock);
            });

        Assert.Equal(DomainError.RemovalIsPermanent, error.Error);
    }

    [Fact]
    public void OpenJoinRequest_ByAnExistingParticipant_IsRejected_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.OpenJoinRequest(fixture.Invitee(0), clock);
            });

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void OpenJoinRequest_ByTheOrganizer_IsRejected_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.OpenJoinRequest(fixture.Organizer, clock);
            });

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void OpenJoinRequest_RejectsASecondOpenRequest_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        UserId requester = UserId.New();
        _ = fixture.Session.OpenJoinRequest(requester, clock);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.OpenJoinRequest(requester, clock);
            });

        Assert.Equal(DomainError.Duplicate, error.Error);
    }

    [Fact]
    public void OpenJoinRequest_IsAllowedAgainAfterAWithdrawal_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        UserId requester = UserId.New();
        JoinRequest first = fixture.Session.OpenJoinRequest(requester, clock);

        fixture.Session.WithdrawJoinRequest(first.Id, clock);
        JoinRequest second = fixture.Session.OpenJoinRequest(requester, clock);

        Assert.Equal(JoinRequestStatus.Withdrawn, first.Status);
        Assert.Equal(JoinRequestStatus.Pending, second.Status);
    }

    [Fact]
    public void ApproveJoinRequest_MakesTheRequesterGoingWithNoAttendanceChoice_D098()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2, maxCapacity: 4);
        UserId requester = UserId.New();
        JoinRequest request = fixture.Session.OpenJoinRequest(requester, clock);
        fixture.Drain();

        Participation row = fixture.Session.ApproveJoinRequest(request.Id, clock);

        Assert.Equal(ParticipationStatus.Going, row.Status);
        Assert.False(row.IsWaitlisted);
        Assert.Equal(ParticipationOrigin.JoinRequest, row.Origin);
        Assert.Equal(JoinRequestStatus.Approved, request.Status);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        Assert.Single(fixture.Drain().OfType<SessionJoinRequestAccepted>());
    }

    [Fact]
    public void ApproveJoinRequest_AtCapacity_MakesTheRequesterGoingWaitlisted_D098()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 1);
        JoinRequest request = fixture.Session.OpenJoinRequest(UserId.New(), clock);
        fixture.Drain();

        Participation row = fixture.Session.ApproveJoinRequest(request.Id, clock);

        Assert.Equal(ParticipationStatus.Going, row.Status);
        Assert.True(row.IsWaitlisted);
        IReadOnlyList<IDomainEvent> raised = fixture.Drain();
        Assert.Single(raised.OfType<SessionJoinRequestAccepted>());
        Assert.Single(raised.OfType<SessionWaitlistAdded>());
    }

    [Fact]
    public void ApproveJoinRequest_WhileTheSessionIsInProgress_IsAllowed_D173()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        JoinRequest request = fixture.Session.OpenJoinRequest(UserId.New(), clock);

        clock.Reset(fixture.Session.StartTime + Duration.FromMinutes(15));
        Participation row = fixture.Session.ApproveJoinRequest(request.Id, clock);

        Assert.Equal(ParticipationStatus.Going, row.Status);
    }

    [Fact]
    public void ApproveJoinRequest_ThatIsNoLongerPending_IsRejected_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        JoinRequest request = fixture.Session.OpenJoinRequest(UserId.New(), clock);
        fixture.Session.RejectJoinRequest(request.Id, clock);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.ApproveJoinRequest(request.Id, clock);
            });

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }

    [Fact]
    public void RejectJoinRequest_NotifiesTheRequester_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        UserId requester = UserId.New();
        JoinRequest request = fixture.Session.OpenJoinRequest(requester, clock);
        fixture.Drain();

        fixture.Session.RejectJoinRequest(request.Id, clock);

        SessionJoinRequestRejected raised = Assert.Single(fixture.Drain().OfType<SessionJoinRequestRejected>());
        Assert.Equal(requester, raised.RequesterId);
        Assert.Equal(JoinRequestStatus.Rejected, request.Status);
        Assert.Null(fixture.Session.Find(ParticipantRef.ForUser(requester)));
    }

    [Fact]
    public void WithdrawJoinRequest_IsSilent_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        JoinRequest request = fixture.Session.OpenJoinRequest(UserId.New(), clock);
        fixture.Drain();

        fixture.Session.WithdrawJoinRequest(request.Id, clock);

        Assert.Equal(JoinRequestStatus.Withdrawn, request.Status);
        Assert.Empty(fixture.Drain());
    }

    [Fact]
    public void LapseJoinRequestsFrom_ClosesAUsersOpenRequestSilently_D065()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        UserId requester = UserId.New();
        JoinRequest request = fixture.Session.OpenJoinRequest(requester, clock);
        fixture.Drain();

        fixture.Session.LapseJoinRequestsFrom(requester, clock);

        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
        Assert.Empty(fixture.Drain());
    }

    [Fact]
    public void UnknownJoinRequest_IsNotFound_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.RejectJoinRequest(JoinRequestId.New(), clock));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void JoinRequest_CarriesAPollTargetForTheSharedShape_D098()
    {
        FakeClock clock = TestSessions.Clock();
        PollId pollId = PollId.New();

        JoinRequest request = JoinRequest.ForPoll(pollId, UserId.New(), clock);

        Assert.Equal(pollId, request.PollId);
        Assert.Null(request.SessionId);
    }

    [Fact]
    public void JoinRequest_ResolvesOnlyOnce_D107()
    {
        FakeClock clock = TestSessions.Clock();
        JoinRequest request = JoinRequest.ForSession(SessionId.New(), UserId.New(), clock);
        request.Approve(clock);

        DomainException error = Assert.Throws<DomainException>(() => request.Reject(clock));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }

    [Fact]
    public void JoinRequest_LapseIsIdempotent_D107()
    {
        FakeClock clock = TestSessions.Clock();
        JoinRequest request = JoinRequest.ForSession(SessionId.New(), UserId.New(), clock);

        request.Lapse(clock);
        request.Lapse(clock);

        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
    }
}
