using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Polls;

/// <summary>
/// The headline poll decisions: polling several candidate times (D-026), the automatic
/// Available → Going mapping on selection (D-034), and polling as the alternative creation mode a
/// finalized session records through its origin poll (D-064).
/// </summary>
public class PollCoreDecisionTests
{
    private static FakeClock SelectionTime => TestPolls.At(TestPolls.Hours(25));

    [Fact]
    public void Create_PollsParticipantsOnSeveralCandidateTimes_D026()
    {
        FakeClock clock = TestPolls.Clock();

        PollUnderTest fixture = TestPolls.New(
            clock,
            inviteeCount: 3,
            optionStartTimes: [TestPolls.Hours(72), TestPolls.Hours(96), TestPolls.Hours(120)]);

        Assert.Equal(PollStatus.Polling, fixture.Poll.Status);
        Assert.Equal(
            new[] { TestPolls.Hours(72), TestPolls.Hours(96), TestPolls.Hours(120) },
            fixture.Poll.Options.Select(option => option.StartTime));
        Assert.Equal(4, fixture.Poll.ActiveParticipantCount);
    }

    [Fact]
    public void Finalize_MakesTheSelectedOptionsAvailableVotersGoing_D034()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 3, minAttendance: 1);
        PollOptionId chosen = fixture.Option(0).Id;

        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(2), chosen, VoteChoice.Unavailable, clock);

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Assert.Equal(ParticipationStatus.Going, session.Find(fixture.InviteeRef(0))!.Status);
        Assert.Equal(ParticipationStatus.Going, session.Find(fixture.InviteeRef(1))!.Status);
        Assert.Equal(ParticipationStatus.Declined, session.Find(fixture.InviteeRef(2))!.Status);
        Assert.Equal(3, session.ActiveGoingCount);
    }

    [Fact]
    public void Finalize_StampsTheOriginPollOnTheSessionThePollBecame_D064()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);

        Session session = fixture.Poll.Finalize(fixture.Option(0).Id, SelectionTime).Session;

        Assert.Equal(fixture.Poll.Id, session.OriginPollId);
        Assert.Equal(PollStatus.Finalized, fixture.Poll.Status);
    }
}
