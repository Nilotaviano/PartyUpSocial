using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Polls;

/// <summary>
/// D-211: a candidate option carries its own length, the poll carries none, and finalization takes
/// the winning option's. D-142's 15 min–24 h grid binds per option.
/// </summary>
public class PollOptionDurationTests
{
    /// <summary>Inside the selection window [Deadline, Deadline + 48 h) for the default 24-hour poll.</summary>
    private static FakeClock SelectionTime => TestPolls.At(TestPolls.Hours(25));

    [Fact]
    public void Create_GivesEachOptionItsOwnDuration_D211()
    {
        FakeClock clock = TestPolls.Clock();

        PollUnderTest fixture = TestPolls.New(
            clock,
            minAttendance: 1,
            optionSpecs:
            [
                TestPolls.Spec(TestPolls.Hours(72), durationMinutes: 45),
                TestPolls.Spec(TestPolls.Hours(96), durationMinutes: 240),
            ]);

        Assert.Equal(45, fixture.Option(0).Duration.TotalMinutes);
        Assert.Equal(240, fixture.Option(1).Duration.TotalMinutes);
        Assert.Equal(
            TestPolls.Hours(72) + Duration.FromMinutes(45),
            fixture.Option(0).Window.End);
    }

    [Fact]
    public void Create_RejectsAnOptionCarryingNoLengthAtAll_D211()
    {
        FakeClock clock = TestPolls.Clock();

        DomainException error = Assert.Throws<DomainException>(
            () => Poll.Create(
                UserId.New(),
                TestPolls.Details(minAttendance: 1),
                PollDeadlineChoice.Hours24,
                [new PollOptionSpec(TestPolls.Hours(72), default), TestPolls.Spec(TestPolls.Hours(96))],
                [UserId.New()],
                clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void SuggestOption_CarriesTheLengthTheSuggesterChose_D211()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        PollOption option = fixture.Poll.SuggestOption(
            fixture.InviteeRef(0), TestPolls.Hours(80), Duration15.FromMinutes(30), clock);

        Assert.Equal(30, option.Duration.TotalMinutes);
    }

    [Fact]
    public void SuggestOption_MergingAnIdenticalInstantKeepsTheRecordedLength_D211()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock,
            optionSpecs:
            [
                TestPolls.Spec(TestPolls.Hours(72), durationMinutes: 45),
                TestPolls.Spec(TestPolls.Hours(96)),
            ]);

        PollOption merged = fixture.Poll.SuggestOption(
            fixture.InviteeRef(0), TestPolls.Hours(72), Duration15.FromMinutes(300), clock);

        Assert.Equal(fixture.Option(0).Id, merged.Id);
        Assert.Equal(45, merged.Duration.TotalMinutes);
        Assert.Equal(2, fixture.Poll.Options.Count);
    }

    [Fact]
    public void Finalize_GivesTheSessionTheWinningOptionsOwnLength_D211()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock,
            minAttendance: 1,
            optionSpecs:
            [
                TestPolls.Spec(TestPolls.Hours(72), durationMinutes: 45),
                TestPolls.Spec(TestPolls.Hours(96), durationMinutes: 300),
            ]);

        Session session = fixture.Poll.Finalize(fixture.Option(1).Id, SelectionTime).Session;

        Assert.Equal(300, session.Duration.TotalMinutes);
        Assert.Equal(TestPolls.Hours(96), session.StartTime);
        Assert.Equal(TestPolls.Hours(96) + Duration.FromMinutes(300), session.EndTime);
    }

    [Fact]
    public void Finalize_SettlesTheD153BranchAgainstTheWinningOptionsOwnLength_D211()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock,
            minAttendance: 1,
            optionSpecs:
            [
                // Both start together; only the short one has ended by the selection instant.
                TestPolls.Spec(TestPolls.Hours(24), durationMinutes: 15),
                TestPolls.Spec(TestPolls.Hours(24) + Duration.FromMinutes(1), durationMinutes: 240),
            ]);
        FakeClock justAfterTheShortOptionEnded = TestPolls.At(TestPolls.Hours(24) + Duration.FromMinutes(30));

        Session ended = fixture.Poll.Finalize(fixture.Option(0).Id, justAfterTheShortOptionEnded).Session;

        Assert.Equal(SessionStatus.Completed, ended.Status);

        FakeClock second = TestPolls.At(TestPolls.Hours(24) + Duration.FromMinutes(30));
        PollUnderTest longFixture = TestPolls.New(
            second,
            minAttendance: 1,
            optionSpecs:
            [
                TestPolls.Spec(TestPolls.Hours(24), durationMinutes: 240),
                TestPolls.Spec(TestPolls.Hours(96)),
            ]);
        Session inProgress = longFixture.Poll.Finalize(
            longFixture.Option(0).Id,
            TestPolls.At(TestPolls.Hours(24) + Duration.FromMinutes(60))).Session;

        Assert.Equal(SessionStatus.Confirmed, inProgress.Status);
        Assert.True(inProgress.IsInProgress(TestPolls.At(TestPolls.Hours(24) + Duration.FromMinutes(60))));
    }
}
