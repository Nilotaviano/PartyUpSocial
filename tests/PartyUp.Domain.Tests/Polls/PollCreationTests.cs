using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Domain.Tests.Polls;

public class PollCreationTests
{
    [Fact]
    public void Create_StartsPollingWithTheOrganizerAsAParticipant_D155()
    {
        FakeClock clock = TestPolls.Clock();

        PollUnderTest fixture = TestPolls.New(clock);

        Assert.Equal(PollStatus.Polling, fixture.Poll.Status);
        PollParticipant organizer = fixture.Row(fixture.OrganizerRef);
        Assert.True(organizer.IsActive);
        Assert.Null(organizer.FirstResponseAt);
    }

    [Fact]
    public void Create_MakesInviteesActiveParticipantsWithNoVotes_D033()
    {
        FakeClock clock = TestPolls.Clock();

        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 3);

        Assert.Equal(4, fixture.Poll.ActiveParticipantCount);
        Assert.All(fixture.Poll.Participants, p => Assert.True(p.IsActive));
        Assert.Empty(fixture.Poll.Votes);
    }

    [Fact]
    public void Create_NotifiesEachInvitee_D112()
    {
        FakeClock clock = TestPolls.Clock();

        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);

        Assert.Equal(2, fixture.Drain().OfType<PollInvitationSent>().Count());
    }

    [Theory]
    [InlineData(PollDeadlineChoice.Hours6, 6)]
    [InlineData(PollDeadlineChoice.Hours12, 12)]
    [InlineData(PollDeadlineChoice.Hours24, 24)]
    [InlineData(PollDeadlineChoice.Hours48, 48)]
    public void Create_SetsTheDeadlineFromTheChosenOffset_D070(PollDeadlineChoice choice, int hours)
    {
        FakeClock clock = TestPolls.Clock();

        PollUnderTest fixture = TestPolls.New(clock, deadlineChoice: choice);

        Assert.Equal(TestPolls.Origin + Duration.FromHours(hours), fixture.Poll.Deadline);
        Assert.Equal(fixture.Poll.Deadline + Duration.FromHours(48), fixture.Poll.SelectionWindowEndsAt);
    }

    [Fact]
    public void Create_RejectsAnUndefinedDeadlineChoice_D070()
    {
        FakeClock clock = TestPolls.Clock();

        DomainException error = Assert.Throws<DomainException>(
            () => Poll.Create(
                UserId.New(),
                TestPolls.Details(),
                (PollDeadlineChoice)99,
                [TestPolls.Hours(72), TestPolls.Hours(96)],
                [UserId.New()],
                clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_RequiresTwoDistinctOptions_D131()
    {
        FakeClock clock = TestPolls.Clock();

        DomainException error = Assert.Throws<DomainException>(
            () => Poll.Create(
                UserId.New(),
                TestPolls.Details(),
                PollDeadlineChoice.Hours24,
                [TestPolls.Hours(72)],
                [UserId.New()],
                clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_CountsDuplicateInstantsOnceAndThenRejectsTheSingleOption_D131()
    {
        FakeClock clock = TestPolls.Clock();

        DomainException error = Assert.Throws<DomainException>(
            () => Poll.Create(
                UserId.New(),
                TestPolls.Details(),
                PollDeadlineChoice.Hours24,
                [TestPolls.Hours(72), TestPolls.Hours(72)],
                [UserId.New()],
                clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_MergesDuplicateInstantsAtCreation_D080()
    {
        FakeClock clock = TestPolls.Clock();

        PollUnderTest fixture = TestPolls.New(
            clock, optionStartTimes: [TestPolls.Hours(72), TestPolls.Hours(96), TestPolls.Hours(72)]);

        Assert.Equal(2, fixture.Poll.Options.Count);
    }

    [Fact]
    public void Create_AllowsOptionsBeforeTheDeadline_D131()
    {
        FakeClock clock = TestPolls.Clock();

        PollUnderTest fixture = TestPolls.New(
            clock,
            deadlineChoice: PollDeadlineChoice.Hours48,
            optionStartTimes: [TestPolls.Hours(2), TestPolls.Hours(6)]);

        Assert.All(fixture.Poll.Options, option => Assert.True(option.StartTime < fixture.Poll.Deadline));
    }

    [Fact]
    public void Create_RejectsMoreThanTwelveDistinctOptions_D080()
    {
        FakeClock clock = TestPolls.Clock();
        Instant[] thirteen = [.. Enumerable.Range(1, 13).Select(TestPolls.Hours)];

        DomainException error = Assert.Throws<DomainException>(
            () => Poll.Create(
                UserId.New(),
                TestPolls.Details(),
                PollDeadlineChoice.Hours24,
                thirteen,
                [UserId.New()],
                clock));

        Assert.Equal(DomainError.LimitExceeded, error.Error);
    }

    [Fact]
    public void Create_AcceptsExactlyTwelveOptions_D080()
    {
        FakeClock clock = TestPolls.Clock();
        Instant[] twelve = [.. Enumerable.Range(1, 12).Select(TestPolls.Hours)];

        PollUnderTest fixture = TestPolls.New(clock, optionStartTimes: twelve);

        Assert.Equal(Poll.MaximumOptions, fixture.Poll.Options.Count);
    }

    [Fact]
    public void Create_RejectsAnOptionBeyondOneYear_D130()
    {
        FakeClock clock = TestPolls.Clock();

        DomainException error = Assert.Throws<DomainException>(
            () => Poll.Create(
                UserId.New(),
                TestPolls.Details(),
                PollDeadlineChoice.Hours24,
                [TestPolls.Hours(72), TestPolls.Origin + Duration.FromDays(366)],
                [UserId.New()],
                clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_AcceptsAnOptionExactlyOneYearOut_D130()
    {
        FakeClock clock = TestPolls.Clock();

        PollUnderTest fixture = TestPolls.New(
            clock, optionStartTimes: [TestPolls.Hours(72), TestPolls.Origin + Duration.FromDays(365)]);

        Assert.Equal(2, fixture.Poll.Options.Count);
    }

    [Fact]
    public void Create_RequiresAnInvitedRegisteredFriendWhenNonPrivate_D125()
    {
        FakeClock clock = TestPolls.Clock();

        DomainException error = Assert.Throws<DomainException>(
            () => Poll.Create(
                UserId.New(),
                TestPolls.Details(isPrivate: false),
                PollDeadlineChoice.Hours24,
                [TestPolls.Hours(72), TestPolls.Hours(96)],
                [],
                clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_AllowsAGuestOnlyPrivatePoll_D125()
    {
        FakeClock clock = TestPolls.Clock();

        PollUnderTest fixture = TestPolls.GuestOnly(clock);

        Assert.True(fixture.Poll.IsPrivate);
        Assert.Single(fixture.Poll.Participants);
    }

    [Fact]
    public void Create_RejectsTheOrganizerAsTheirOwnInvitee_D125()
    {
        FakeClock clock = TestPolls.Clock();
        UserId organizer = UserId.New();

        DomainException error = Assert.Throws<DomainException>(
            () => Poll.Create(
                organizer,
                TestPolls.Details(),
                PollDeadlineChoice.Hours24,
                [TestPolls.Hours(72), TestPolls.Hours(96)],
                [organizer],
                clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void Create_RejectsADuplicateInvitee_D125()
    {
        FakeClock clock = TestPolls.Clock();
        UserId invitee = UserId.New();

        DomainException error = Assert.Throws<DomainException>(
            () => Poll.Create(
                UserId.New(),
                TestPolls.Details(),
                PollDeadlineChoice.Hours24,
                [TestPolls.Hours(72), TestPolls.Hours(96)],
                [invitee, invitee],
                clock));

        Assert.Equal(DomainError.Duplicate, error.Error);
    }

    [Fact]
    public void Create_CarriesEveryEventualSessionDetail_D154()
    {
        FakeClock clock = TestPolls.Clock();
        UserId organizer = UserId.New();

        Poll poll = Poll.Create(
            organizer,
            TestPolls.Details(
                minAttendance: 3,
                maxCapacity: 5,
                durationMinutes: 90,
                title: "Deep Rock",
                platform: Platform.Steam,
                game: "Deep Rock Galactic",
                notes: "Bring beer"),
            PollDeadlineChoice.Hours12,
            [TestPolls.Hours(72), TestPolls.Hours(96)],
            [UserId.New()],
            clock);

        Assert.Equal("Deep Rock", poll.Title.Value);
        Assert.Equal(90, poll.Duration.TotalMinutes);
        Assert.Equal(Platform.Steam, poll.Platform);
        Assert.Equal("Deep Rock Galactic", poll.Game!.Name.Value);
        Assert.Equal("Bring beer", poll.Notes.Value);
        Assert.Equal(3, poll.MinAttendance);
        Assert.Equal(5, poll.MaxCapacity);
    }

    [Fact]
    public void Create_AttributesInitialOptionsToTheOrganizer_D033()
    {
        FakeClock clock = TestPolls.Clock();

        PollUnderTest fixture = TestPolls.New(clock);

        Assert.All(fixture.Poll.Options, option => Assert.True(option.WasSuggestedBy(fixture.OrganizerRef)));
    }
}
