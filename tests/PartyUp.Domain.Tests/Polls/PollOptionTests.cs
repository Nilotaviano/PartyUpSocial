using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Domain.Tests.Polls;

public class PollOptionTests
{
    [Fact]
    public void SuggestOption_AddsTheOptionImmediatelyWithoutApproval_D051()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        PollOption option = fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(80), TestPolls.DefaultDuration, clock);

        Assert.Equal(3, fixture.Poll.Options.Count);
        Assert.True(option.WasSuggestedBy(fixture.InviteeRef(0)));
    }

    [Fact]
    public void SuggestOption_NotifiesTheOrganizerOnly_D120()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Drain();

        fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(80), TestPolls.DefaultDuration, clock);

        PollOptionSuggested suggested = Assert.Single(fixture.Drain().OfType<PollOptionSuggested>());
        Assert.Equal(fixture.InviteeRef(0), suggested.SuggestedBy);
    }

    [Fact]
    public void SuggestOption_LetsAGuestVoterSuggest_D033()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (_, ParticipantRef guest) = PollGuests.Claim(fixture.Poll, clock, "Robin");

        PollOption option = fixture.Poll.SuggestOption(guest, TestPolls.Hours(80), TestPolls.DefaultDuration, clock);

        Assert.True(option.SuggestedBy.IsGuest);
        Assert.True(option.WasSuggestedBy(guest));
    }

    [Fact]
    public void SuggestOption_MergesAnIdenticalInstantAndReturnsTheExistingOption_D080()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        PollOption existing = fixture.Option(0);
        fixture.Drain();

        PollOption merged = fixture.Poll.SuggestOption(fixture.InviteeRef(0), existing.StartTime, TestPolls.DefaultDuration, clock);

        Assert.Equal(existing.Id, merged.Id);
        Assert.Equal(2, fixture.Poll.Options.Count);
        Assert.Empty(fixture.Drain().OfType<PollOptionSuggested>());
    }

    [Fact]
    public void SuggestOption_MergesADuplicateEvenAtTheTwelveOptionCap_D080()
    {
        FakeClock clock = TestPolls.Clock();
        Instant[] twelve = [.. Enumerable.Range(1, 12).Select(TestPolls.Hours)];
        PollUnderTest fixture = TestPolls.New(clock, optionStartTimes: twelve);

        PollOption merged = fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(1), TestPolls.DefaultDuration, clock);

        Assert.Equal(fixture.Option(0).Id, merged.Id);
        Assert.Equal(Poll.MaximumOptions, fixture.Poll.Options.Count);
    }

    [Fact]
    public void SuggestOption_RejectsANewInstantAtTheTwelveOptionCap_D171()
    {
        FakeClock clock = TestPolls.Clock();
        Instant[] twelve = [.. Enumerable.Range(1, 12).Select(TestPolls.Hours)];
        PollUnderTest fixture = TestPolls.New(clock, optionStartTimes: twelve);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(13), TestPolls.DefaultDuration, clock));

        Assert.Equal(DomainError.LimitExceeded, error.Error);
        Assert.Equal(Poll.MaximumOptions, fixture.Poll.Options.Count);
    }

    [Fact]
    public void SuggestOption_AcceptsANewInstantAtElevenOptions_D171()
    {
        FakeClock clock = TestPolls.Clock();
        Instant[] eleven = [.. Enumerable.Range(1, 11).Select(TestPolls.Hours)];
        PollUnderTest fixture = TestPolls.New(clock, optionStartTimes: eleven);

        fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(13), TestPolls.DefaultDuration, clock);

        Assert.Equal(Poll.MaximumOptions, fixture.Poll.Options.Count);
    }

    [Fact]
    public void SuggestOption_RejectsAnInstantBeyondOneYearFromCreation_D130()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.SuggestOption(
                fixture.InviteeRef(0), TestPolls.Origin + Duration.FromDays(366), TestPolls.DefaultDuration, clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void SuggestOption_IsRejectedAtTheDeadlineWhileTheStatusIsStillPolling_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock late = TestPolls.At(TestPolls.DefaultDeadline);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(80), TestPolls.DefaultDuration, late));

        Assert.Equal(DomainError.WindowClosed, error.Error);
        Assert.Equal(PollStatus.Polling, fixture.Poll.Status);
    }

    [Fact]
    public void SuggestOption_IsRejectedOnAClosedPoll_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock closed = TestPolls.At(TestPolls.DefaultDeadline + Duration.FromMinutes(1));
        fixture.Poll.ApplyDueTransitions(closed);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(80), TestPolls.DefaultDuration, closed));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void SuggestOption_RejectsANonParticipant_D033()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.SuggestOption(ParticipantRef.ForUser(UserId.New()), TestPolls.Hours(80), TestPolls.DefaultDuration, clock));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void SuggestOption_RejectsARemovedParticipant_D077()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.RemoveParticipant(fixture.InviteeRef(0), clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.SuggestOption(fixture.InviteeRef(0), TestPolls.Hours(80), TestPolls.DefaultDuration, clock));

        Assert.Equal(DomainError.RemovalIsPermanent, error.Error);
    }

    [Fact]
    public void DeleteOption_RemovesAnyOptionWithNoFloor_D080()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        fixture.Poll.DeleteOption(fixture.Option(0).Id, clock);
        fixture.Poll.DeleteOption(fixture.Option(0).Id, clock);

        Assert.Empty(fixture.Poll.Options);
    }

    [Fact]
    public void DeleteOption_IsSilent_D088()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Drain();

        fixture.Poll.DeleteOption(fixture.Option(0).Id, clock);

        Assert.Empty(fixture.Drain());
    }

    [Fact]
    public void DeleteOption_CascadesItsVotes_D080()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        PollOptionId doomed = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), doomed, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(1).Id, VoteChoice.Maybe, clock);

        fixture.Poll.DeleteOption(doomed, clock);

        Assert.Single(fixture.Poll.Votes);
        Assert.DoesNotContain(fixture.Poll.Votes, v => v.OptionId == doomed);
    }

    [Fact]
    public void DeleteOption_NeverRecomputesFirstResponseAt_D172()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        PollOptionId doomed = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), doomed, VoteChoice.Available, clock);
        Instant firstResponse = fixture.Row(fixture.InviteeRef(0)).FirstResponseAt!.Value;

        clock.Advance(Duration.FromHours(1));
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(1).Id, VoteChoice.Maybe, clock);
        fixture.Poll.DeleteOption(doomed, clock);

        Assert.Equal(firstResponse, fixture.Row(fixture.InviteeRef(0)).FirstResponseAt);
    }

    [Fact]
    public void DeleteOption_IsAllowedWhileClosed_D088()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock closed = TestPolls.At(TestPolls.DefaultDeadline + Duration.FromMinutes(5));

        fixture.Poll.DeleteOption(fixture.Option(0).Id, closed);

        Assert.Equal(PollStatus.Closed, fixture.Poll.Status);
        Assert.Single(fixture.Poll.Options);
    }

    [Fact]
    public void DeleteOption_IsRejectedOnACanceledPoll_D156()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Cancel(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.DeleteOption(fixture.Option(0).Id, clock));

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void DeleteOption_RejectsAnUnknownOption_D080()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.DeleteOption(PollOptionId.New(), clock));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void DeleteOption_LeavesAnEmptiedPollOnlyCancelableOrExpirable_D080()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.DeleteOption(fixture.Option(0).Id, clock);
        PollOptionId last = fixture.Option(0).Id;
        fixture.Poll.DeleteOption(last, clock);

        FakeClock afterDeadline = TestPolls.At(TestPolls.DefaultDeadline + Duration.FromMinutes(1));
        fixture.Poll.ApplyDueTransitions(afterDeadline);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Finalize(last, afterDeadline));

        Assert.Equal(DomainError.NotFound, error.Error);
        fixture.Poll.Cancel(afterDeadline);
        Assert.Equal(PollStatus.Canceled, fixture.Poll.Status);
    }

    [Fact]
    public void Options_StaySelectableAfterTheirStartTimePasses_D140()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock, deadlineChoice: PollDeadlineChoice.Hours24, optionStartTimes: [TestPolls.Hours(1), TestPolls.Hours(2)]);

        FakeClock afterOptions = TestPolls.At(TestPolls.Hours(25));

        Assert.True(fixture.Option(0).HasElapsed(afterOptions.GetCurrentInstant()));
        fixture.Poll.ApplyDueTransitions(afterOptions);
        Assert.Equal(PollStatus.Closed, fixture.Poll.Status);
    }
}
