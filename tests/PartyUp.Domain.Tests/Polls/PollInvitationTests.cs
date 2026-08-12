using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Domain.Tests.Polls;

/// <summary>
/// D-193: the organizer's mid-flight registered invite, mirroring
/// <c>SessionInvitationTests</c>-style coverage of <c>Session.Invite</c> but without a Declined arm —
/// polls have none to revive.
/// </summary>
public class PollInvitationTests
{
    [Fact]
    public void Invite_AddsAnActiveParticipantWithNoVotes_D193_D033()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        UserId invitee = UserId.New();

        PollParticipant participant = fixture.Poll.Invite(invitee, clock);

        Assert.True(participant.IsActive);
        Assert.Null(participant.FirstResponseAt);
        Assert.Empty(fixture.Poll.VotesOf(participant.Participant));
        Assert.Same(participant, fixture.Poll.FindParticipant(participant.Participant));
    }

    [Fact]
    public void Invite_RaisesPollInvitationSentForTheNewInviteeOnly_D193_D112()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Drain();
        UserId invitee = UserId.New();

        fixture.Poll.Invite(invitee, clock);

        PollInvitationSent raised = Assert.Single(fixture.Drain().OfType<PollInvitationSent>());
        Assert.Equal(invitee, raised.InviteeId);
        Assert.Equal(fixture.Poll.Id, raised.PollId);
    }

    /// <summary>
    /// The guard is status, not clock (§1): once the due Close sweep has applied, a Closed-but-not-
    /// yet-Expired poll rejects with InvalidTransition rather than a clock-based WindowClosed.
    /// </summary>
    [Fact]
    public void Invite_RejectsAClosedPoll_D193()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        clock.Advance(Duration.FromHours(24));
        fixture.Poll.ApplyDueTransitions(clock);
        Assert.Equal(PollStatus.Closed, fixture.Poll.Status);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Invite(UserId.New(), clock));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }

    [Fact]
    public void Invite_RejectsAnExpiredPoll_D193()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        clock.Advance(Duration.FromHours(24));
        fixture.Poll.ApplyDueTransitions(clock);
        clock.Advance(Duration.FromHours(48));
        fixture.Poll.ApplyDueTransitions(clock);
        Assert.Equal(PollStatus.Expired, fixture.Poll.Status);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Invite(UserId.New(), clock));

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void Invite_RejectsACanceledPoll_D193()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Cancel(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Invite(UserId.New(), clock));

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void Invite_RejectsAFinalizedPoll_D193()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);
        fixture.Poll.Finalize(fixture.Option(0).Id, clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Invite(UserId.New(), clock));

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void Invite_RejectsTheOrganizerAsTheirOwnInvitee_D193()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Invite(fixture.Organizer, clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void Invite_RefusesABlockedCounterpart_D193_D065_D077()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        UserId blocked = UserId.New();

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Invite(blocked, clock, _ => true));

        Assert.Equal(DomainError.Blocked, error.Error);
        Assert.Null(fixture.Poll.FindParticipant(ParticipantRef.ForUser(blocked)));
    }

    [Fact]
    public void Invite_RefusesAnAlreadyActiveParticipant_D193()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Invite(fixture.Invitee(0), clock));

        Assert.Equal(DomainError.Duplicate, error.Error);
    }

    [Fact]
    public void Invite_NeverReInvitesARemovedParticipant_D193()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.RemoveParticipant(fixture.InviteeRef(0), clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Invite(fixture.Invitee(0), clock));

        Assert.Equal(DomainError.RemovalIsPermanent, error.Error);
        Assert.True(fixture.Row(fixture.InviteeRef(0)).IsRemoved);
    }

    /// <summary>
    /// D-119/D-172: a mid-flight invitee's FirstResponseAt is untouched by Invite itself and only
    /// stamps at their first vote — arriving late costs allocation priority and nothing else.
    /// </summary>
    [Fact]
    public void Invite_LateInviteesFirstResponseAtStampsOnlyAtTheirFirstVote_D119_D172()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        clock.Advance(Duration.FromHours(1));
        UserId lateInvitee = UserId.New();

        PollParticipant participant = fixture.Poll.Invite(lateInvitee, clock);
        Assert.Null(participant.FirstResponseAt);

        clock.Advance(Duration.FromMinutes(30));
        fixture.Poll.Vote(participant.Participant, fixture.Option(0).Id, VoteChoice.Available, clock);

        Assert.Equal(clock.GetCurrentInstant(), fixture.Row(participant.Participant).FirstResponseAt);
    }
}
