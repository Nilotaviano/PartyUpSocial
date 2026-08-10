using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Domain.Tests.Polls;

public class PollVotingTests
{
    [Fact]
    public void Vote_RecordsTheChoiceOnTheOption_D033()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Maybe, clock);

        PollVote vote = Assert.Single(fixture.Poll.Votes);
        Assert.Equal(VoteChoice.Maybe, vote.Choice);
        Assert.Equal(fixture.InviteeRef(0), vote.Participant);
    }

    [Fact]
    public void Vote_LetsTheOrganizerVoteLikeAnyoneElse_D155()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        fixture.Poll.Vote(fixture.OrganizerRef, fixture.Option(0).Id, VoteChoice.Unavailable, clock);

        Assert.Equal(VoteChoice.Unavailable, fixture.Poll.FindVote(fixture.Option(0).Id, fixture.OrganizerRef)!.Choice);
        Assert.NotNull(fixture.Row(fixture.OrganizerRef).FirstResponseAt);
    }

    [Fact]
    public void Vote_LetsAGuestVote_D033()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (_, ParticipantRef guest) = PollGuests.Claim(fixture.Poll, clock, "Robin");

        fixture.Poll.Vote(guest, fixture.Option(0).Id, VoteChoice.Available, clock);

        Assert.Equal(VoteChoice.Available, fixture.Poll.FindVote(fixture.Option(0).Id, guest)!.Choice);
    }

    [Fact]
    public void Vote_RevisesAnExistingVoteInPlace_D033()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);

        clock.Advance(Duration.FromHours(2));
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Unavailable, clock);

        PollVote vote = Assert.Single(fixture.Poll.Votes);
        Assert.Equal(VoteChoice.Unavailable, vote.Choice);
    }

    [Fact]
    public void Vote_SetsFirstResponseAtOnceAndNeverAgain_D172()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);
        Instant first = fixture.Row(fixture.InviteeRef(0)).FirstResponseAt!.Value;

        clock.Advance(Duration.FromHours(3));
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Maybe, clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(1).Id, VoteChoice.Available, clock);

        Assert.Equal(TestPolls.Origin, first);
        Assert.Equal(first, fixture.Row(fixture.InviteeRef(0)).FirstResponseAt);
    }

    [Fact]
    public void SuggestOption_RecordsTheSuggestersOwnAvailableVote_D185()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        PollOption option = fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(80), clock);

        PollVote vote = fixture.Poll.FindVote(option.Id, fixture.InviteeRef(0))!;
        Assert.Equal(VoteChoice.Available, vote.Choice);
        Assert.Contains(vote, fixture.Poll.Votes);
    }

    [Fact]
    public void SuggestOption_AlsoRecordsTheAutoVoteOnTheDedupPath_D185_D080()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        PollOption existing = fixture.Option(0);

        PollOption merged = fixture.Poll.SuggestOption(fixture.InviteeRef(1), existing.StartTime, clock);

        Assert.Equal(existing.Id, merged.Id);
        Assert.Equal(VoteChoice.Available, fixture.Poll.FindVote(merged.Id, fixture.InviteeRef(1))!.Choice);
    }

    [Fact]
    public void SuggestOption_OverwritesAPriorUnavailableVoteOnTheDedupPath_D185()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        PollOption existing = fixture.Option(0);
        fixture.Poll.Vote(fixture.InviteeRef(0), existing.Id, VoteChoice.Unavailable, clock);

        fixture.Poll.SuggestOption(fixture.InviteeRef(0), existing.StartTime, clock);

        Assert.Equal(VoteChoice.Available, fixture.Poll.FindVote(existing.Id, fixture.InviteeRef(0))!.Choice);
        Assert.Single(fixture.Poll.VotesOf(fixture.InviteeRef(0)));
    }

    [Fact]
    public void SuggestOption_GivesAGuestSuggesterTheSameAutoVote_D185_D033()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (_, ParticipantRef guest) = PollGuests.Claim(fixture.Poll, clock, "Robin");

        PollOption option = fixture.Poll.SuggestOption(guest, TestPolls.Hours(80), clock);

        Assert.Equal(VoteChoice.Available, fixture.Poll.FindVote(option.Id, guest)!.Choice);
    }

    [Fact]
    public void SuggestOption_StampsFirstResponseAt_D185_D172()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(80), clock);

        Assert.NotNull(fixture.Row(fixture.InviteeRef(0)).FirstResponseAt);
    }

    [Fact]
    public void SuggestOption_KeepsTheOriginalFirstResponseAtWhenTheyAlreadyResponded_D185_D172()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Maybe, clock);
        Instant first = fixture.Row(fixture.InviteeRef(0)).FirstResponseAt!.Value;

        clock.Advance(Duration.FromHours(3));
        fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(80), clock);

        Assert.Equal(first, fixture.Row(fixture.InviteeRef(0)).FirstResponseAt);
    }

    [Fact]
    public void SuggestOption_RejectedAtTheCapRecordsNoVoteAndLeavesExistingVotesUntouched_D185_D171()
    {
        FakeClock clock = TestPolls.Clock();
        Instant[] twelve = [.. Enumerable.Range(1, 12).Select(TestPolls.Hours)];
        PollUnderTest fixture = TestPolls.New(clock, optionStartTimes: twelve);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Unavailable, clock);

        Assert.Throws<DomainException>(
            () => fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(13), clock));

        Assert.Single(fixture.Poll.Votes);
        Assert.Equal(VoteChoice.Unavailable, fixture.Poll.FindVote(fixture.Option(0).Id, fixture.InviteeRef(0))!.Choice);
    }

    [Fact]
    public void Vote_IsRejectedOneMinuteAfterTheDeadlineWhileTheStatusIsStillPolling_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock late = TestPolls.At(TestPolls.DefaultDeadline + Duration.FromMinutes(1));

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, late));

        Assert.Equal(DomainError.WindowClosed, error.Error);
        Assert.Equal(PollStatus.Polling, fixture.Poll.Status);
        Assert.Empty(fixture.Poll.Votes);
    }

    [Fact]
    public void Vote_IsRejectedAtExactlyTheDeadline_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock atDeadline = TestPolls.At(TestPolls.DefaultDeadline);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, atDeadline));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void Vote_IsAcceptedOneMinuteBeforeTheDeadline_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock justInTime = TestPolls.At(TestPolls.DefaultDeadline - Duration.FromMinutes(1));

        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, justInTime);

        Assert.Single(fixture.Poll.Votes);
    }

    [Fact]
    public void Vote_IsRejectedOnACanceledPollBeforeTheDeadline_D156()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Cancel(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock));

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void Vote_RejectsAnUnknownOption_D033()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Vote(fixture.InviteeRef(0), PollOptionId.New(), VoteChoice.Available, clock));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void Vote_RejectsAnUndefinedChoice_D033()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, (VoteChoice)42, clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Vote_RejectsARemovedParticipant_D092()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.RemoveParticipant(fixture.InviteeRef(0), clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock));

        Assert.Equal(DomainError.RemovalIsPermanent, error.Error);
    }

    [Fact]
    public void RemoveParticipant_IsIdempotent_D077()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        fixture.Poll.RemoveParticipant(fixture.InviteeRef(0), clock);
        fixture.Poll.RemoveParticipant(fixture.InviteeRef(0), clock);

        Assert.True(fixture.Row(fixture.InviteeRef(0)).IsRemoved);
        Assert.Equal(2, fixture.Poll.ActiveParticipantCount);
    }

    [Fact]
    public void RemoveParticipant_KeepsTheirCastVotesForRendering_D077()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);

        fixture.Poll.RemoveParticipant(fixture.InviteeRef(0), clock);

        Assert.Single(fixture.Poll.VotesOf(fixture.InviteeRef(0)));
    }

    [Fact]
    public void VotedUnavailableThroughout_IsFalseWithNoVotesYet_D018()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        Assert.False(fixture.Poll.VotedUnavailableThroughout(fixture.InviteeRef(0)));
    }

    [Fact]
    public void VotedUnavailableThroughout_IsTrueOnlyWhenEveryVoteIsUnavailable_D051()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Unavailable, clock);

        Assert.True(fixture.Poll.VotedUnavailableThroughout(fixture.InviteeRef(0)));

        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(1).Id, VoteChoice.Maybe, clock);

        Assert.False(fixture.Poll.VotedUnavailableThroughout(fixture.InviteeRef(0)));
    }
}
