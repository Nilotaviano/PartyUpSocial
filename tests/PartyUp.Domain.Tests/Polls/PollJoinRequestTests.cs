using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Polls;

public class PollJoinRequestTests
{
    [Fact]
    public void OpenJoinRequest_NotifiesTheOrganizer_D118()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Drain();

        JoinRequest request = fixture.Poll.OpenJoinRequest(UserId.New(), clock);

        Assert.True(request.IsPending);
        Assert.Equal(fixture.Poll.Id, request.PollId);
        Assert.Single(fixture.Drain().OfType<PollJoinRequestReceived>());
    }

    [Fact]
    public void ApproveJoinRequest_MakesTheRequesterAnActiveVoter_D118()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        UserId requester = UserId.New();
        JoinRequest request = fixture.Poll.OpenJoinRequest(requester, clock);
        fixture.Drain();

        PollParticipant participant = fixture.Poll.ApproveJoinRequest(request.Id, clock);

        Assert.True(participant.IsActive);
        Assert.Equal(JoinRequestStatus.Approved, request.Status);
        Assert.Single(fixture.Drain().OfType<PollJoinRequestAccepted>());
    }

    [Fact]
    public void ApproveJoinRequest_GivesAccessToEveryCurrentOptionAndSetsNoResponseTime_D118()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        UserId requester = UserId.New();
        JoinRequest request = fixture.Poll.OpenJoinRequest(requester, clock);
        PollParticipant participant = fixture.Poll.ApproveJoinRequest(request.Id, clock);

        Assert.Null(participant.FirstResponseAt);

        foreach (PollOption option in fixture.Poll.Options)
        {
            fixture.Poll.Vote(ParticipantRef.ForUser(requester), option.Id, VoteChoice.Available, clock);
        }

        Assert.Equal(fixture.Poll.Options.Count, fixture.Poll.VotesOf(ParticipantRef.ForUser(requester)).Count);
        Assert.NotNull(fixture.Poll.FindParticipant(ParticipantRef.ForUser(requester))!.FirstResponseAt);
    }

    [Fact]
    public void OpenJoinRequest_IsRejectedOnceVotingCloses_D118()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock atDeadline = TestPolls.At(TestPolls.DefaultDeadline);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.OpenJoinRequest(UserId.New(), atDeadline));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void ApproveJoinRequest_IsRejectedOnceVotingCloses_D118()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        JoinRequest request = fixture.Poll.OpenJoinRequest(UserId.New(), clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.ApproveJoinRequest(request.Id, TestPolls.At(TestPolls.DefaultDeadline)));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void OpenJoinRequest_RejectsAnExistingParticipant_D118()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.OpenJoinRequest(fixture.Invitee(0), clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void OpenJoinRequest_RejectsTheOrganizer_D118()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.OpenJoinRequest(fixture.Organizer, clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void OpenJoinRequest_RejectsARemovedParticipant_D092()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.RemoveParticipant(fixture.InviteeRef(0), clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.OpenJoinRequest(fixture.Invitee(0), clock));

        Assert.Equal(DomainError.RemovalIsPermanent, error.Error);
    }

    [Fact]
    public void OpenJoinRequest_RejectsASecondOpenRequest_D118()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        UserId requester = UserId.New();
        fixture.Poll.OpenJoinRequest(requester, clock);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Poll.OpenJoinRequest(requester, clock));

        Assert.Equal(DomainError.Duplicate, error.Error);
    }

    [Fact]
    public void RejectJoinRequest_NotifiesTheRequester_D107()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        JoinRequest request = fixture.Poll.OpenJoinRequest(UserId.New(), clock);
        fixture.Drain();

        fixture.Poll.RejectJoinRequest(request.Id, clock);

        Assert.Equal(JoinRequestStatus.Rejected, request.Status);
        Assert.Single(fixture.Drain().OfType<PollJoinRequestRejected>());
    }

    [Fact]
    public void WithdrawJoinRequest_IsSilent_D107()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        JoinRequest request = fixture.Poll.OpenJoinRequest(UserId.New(), clock);
        fixture.Drain();

        fixture.Poll.WithdrawJoinRequest(request.Id, clock);

        Assert.Equal(JoinRequestStatus.Withdrawn, request.Status);
        Assert.Empty(fixture.Drain());
    }

    [Fact]
    public void LapseJoinRequestsFrom_ClosesTheRequestSilently_D065()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        UserId requester = UserId.New();
        JoinRequest request = fixture.Poll.OpenJoinRequest(requester, clock);
        fixture.Drain();

        fixture.Poll.LapseJoinRequestsFrom(requester, clock);

        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
        Assert.Empty(fixture.Drain());
    }

    [Fact]
    public void OpenJoinRequest_IsRejectedOnACanceledPoll_D156()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Cancel(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.OpenJoinRequest(UserId.New(), clock));

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void ApproveJoinRequest_IsRejectedOnACanceledPoll_D156()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        JoinRequest request = fixture.Poll.OpenJoinRequest(UserId.New(), clock);
        fixture.Poll.Cancel(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.ApproveJoinRequest(request.Id, clock));

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void ApproveJoinRequest_RejectsAnAlreadyResolvedRequest_D107()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        JoinRequest request = fixture.Poll.OpenJoinRequest(UserId.New(), clock);
        fixture.Poll.RejectJoinRequest(request.Id, clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.ApproveJoinRequest(request.Id, clock));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }
}
