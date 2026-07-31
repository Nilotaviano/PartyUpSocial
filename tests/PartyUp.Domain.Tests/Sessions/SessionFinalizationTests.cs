using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

public class SessionFinalizationTests
{
    private static SessionDetails PastOption(Instant start, int minAttendance = 2, int? maxCapacity = null) =>
        TestSessions.Details(start, minAttendance, maxCapacity);

    [Fact]
    public void FromPollFinalization_IgnoresTheD130StartWindow_D153()
    {
        FakeClock clock = TestSessions.Clock();
        Instant startedYesterday = TestSessions.Origin - Duration.FromHours(20);

        Session session = Session.FromPollFinalization(
            PollId.New(),
            UserId.New(),
            PastOption(startedYesterday, minAttendance: 1),
            [],
            SessionStatus.Completed,
            clock);

        Assert.Equal(startedYesterday, session.StartTime);
        Assert.Equal(SessionStatus.Completed, session.Status);
    }

    [Fact]
    public void FromPollFinalization_CanMintACompletedSessionWithoutAPriorConfirmed_D153()
    {
        FakeClock clock = TestSessions.Clock();
        UserId organizer = UserId.New();

        Session session = Session.FromPollFinalization(
            PollId.New(),
            organizer,
            PastOption(TestSessions.Origin - Duration.FromHours(10), minAttendance: 1),
            [new FinalizedParticipation(ParticipantRef.ForUser(UserId.New()), ParticipationStatus.Going, false)],
            SessionStatus.Completed,
            clock);

        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.True(session.IsRetainedInHistory);
        Assert.Equal(2, session.ActiveGoingCount);
    }

    [Fact]
    public void FromPollFinalization_CanMintAnExpiredSessionThatIsNotRetained_D164()
    {
        FakeClock clock = TestSessions.Clock();

        Session session = Session.FromPollFinalization(
            PollId.New(),
            UserId.New(),
            PastOption(TestSessions.Origin - Duration.FromHours(10), minAttendance: 4),
            [],
            SessionStatus.Expired,
            clock);

        Assert.Equal(SessionStatus.Expired, session.Status);
        Assert.False(session.IsRetainedInHistory);
    }

    [Fact]
    public void FromPollFinalization_RejectsACanceledInitialStatus_D153()
    {
        FakeClock clock = TestSessions.Clock();

        DomainException error = Assert.Throws<DomainException>(
            () => Session.FromPollFinalization(
                PollId.New(), UserId.New(), PastOption(TestSessions.DefaultStart), [], SessionStatus.Canceled, clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void FromPollFinalization_AddsTheOrganizerGoingRegardlessOfTheirVote_D155()
    {
        FakeClock clock = TestSessions.Clock();
        UserId organizer = UserId.New();

        Session session = Session.FromPollFinalization(
            PollId.New(),
            organizer,
            PastOption(TestSessions.DefaultStart, minAttendance: 1),
            [],
            SessionStatus.Confirmed,
            clock);

        Participation row = session.Find(ParticipantRef.ForUser(organizer))!;
        Assert.Equal(ParticipationStatus.Going, row.Status);
        Assert.False(row.IsWaitlisted);
        Assert.Equal(ParticipationOrigin.PollFinalization, row.Origin);
    }

    [Fact]
    public void FromPollFinalization_KeepsAnExplicitOrganizerRow_D155()
    {
        FakeClock clock = TestSessions.Clock();
        UserId organizer = UserId.New();

        Session session = Session.FromPollFinalization(
            PollId.New(),
            organizer,
            PastOption(TestSessions.DefaultStart, minAttendance: 1),
            [new FinalizedParticipation(ParticipantRef.ForUser(organizer), ParticipationStatus.Going, false)],
            SessionStatus.Confirmed,
            clock);

        Assert.Single(session.Participations);
    }

    [Fact]
    public void FromPollFinalization_PreservesTheD119OverflowWaitlist_D119()
    {
        FakeClock clock = TestSessions.Clock();
        UserId organizer = UserId.New();
        UserId available = UserId.New();
        UserId overflow = UserId.New();

        Session session = Session.FromPollFinalization(
            PollId.New(),
            organizer,
            PastOption(TestSessions.DefaultStart, minAttendance: 1, maxCapacity: 2),
            [
                new FinalizedParticipation(ParticipantRef.ForUser(available), ParticipationStatus.Going, false),
                new FinalizedParticipation(ParticipantRef.ForUser(overflow), ParticipationStatus.Maybe, true),
            ],
            SessionStatus.Confirmed,
            clock);

        Assert.Equal(2, session.ActiveOccupancy);
        Assert.Equal(1, session.WaitlistCount);
        Assert.True(session.Find(ParticipantRef.ForUser(overflow))!.IsWaitlisted);
        Assert.Equal(ParticipationStatus.Maybe, session.Find(ParticipantRef.ForUser(overflow))!.Status);
    }

    [Fact]
    public void FromPollFinalization_RejectsAnOverAllocatedRoster_D119()
    {
        FakeClock clock = TestSessions.Clock();

        DomainException error = Assert.Throws<DomainException>(
            () => Session.FromPollFinalization(
                PollId.New(),
                UserId.New(),
                PastOption(TestSessions.DefaultStart, minAttendance: 1, maxCapacity: 2),
                [
                    new FinalizedParticipation(ParticipantRef.ForUser(UserId.New()), ParticipationStatus.Going, false),
                    new FinalizedParticipation(ParticipantRef.ForUser(UserId.New()), ParticipationStatus.Maybe, false),
                ],
                SessionStatus.Confirmed,
                clock));

        Assert.Equal(DomainError.CapacityExceeded, error.Error);
    }

    [Fact]
    public void FromPollFinalization_RejectsARemovedPollParticipant_D092()
    {
        FakeClock clock = TestSessions.Clock();

        DomainException error = Assert.Throws<DomainException>(
            () => Session.FromPollFinalization(
                PollId.New(),
                UserId.New(),
                PastOption(TestSessions.DefaultStart),
                [new FinalizedParticipation(ParticipantRef.ForUser(UserId.New()), ParticipationStatus.Removed, false)],
                SessionStatus.Unconfirmed,
                clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void FromPollFinalization_RejectsAWaitlistedNonResponder_D084()
    {
        FakeClock clock = TestSessions.Clock();

        DomainException error = Assert.Throws<DomainException>(
            () => Session.FromPollFinalization(
                PollId.New(),
                UserId.New(),
                PastOption(TestSessions.DefaultStart),
                [new FinalizedParticipation(ParticipantRef.ForUser(UserId.New()), ParticipationStatus.Pending, true)],
                SessionStatus.Unconfirmed,
                clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void FromPollFinalization_LeavesNonVotersPendingForeverOnAStartedSession_D081()
    {
        FakeClock clock = TestSessions.Clock();
        UserId nonVoter = UserId.New();
        Instant started = TestSessions.Origin - Duration.FromMinutes(30);

        Session session = Session.FromPollFinalization(
            PollId.New(),
            UserId.New(),
            PastOption(started, minAttendance: 1),
            [new FinalizedParticipation(ParticipantRef.ForUser(nonVoter), ParticipationStatus.Pending, false)],
            SessionStatus.Confirmed,
            clock);

        DomainException error = Assert.Throws<DomainException>(
            () => session.Rsvp(ParticipantRef.ForUser(nonVoter), ParticipationStatus.Going, clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
        Assert.Equal(ParticipationStatus.Pending, session.Find(ParticipantRef.ForUser(nonVoter))!.Status);
    }

    [Fact]
    public void FromPollFinalization_SweeperReRunsAreNoOps_D153()
    {
        FakeClock clock = TestSessions.Clock();
        Session completed = Session.FromPollFinalization(
            PollId.New(),
            UserId.New(),
            PastOption(TestSessions.Origin - Duration.FromHours(10), minAttendance: 1),
            [],
            SessionStatus.Completed,
            clock);
        Session expired = Session.FromPollFinalization(
            PollId.New(),
            UserId.New(),
            PastOption(TestSessions.Origin - Duration.FromHours(10), minAttendance: 5),
            [],
            SessionStatus.Expired,
            clock);

        completed.ApplyDueTransitions(clock);
        expired.ApplyDueTransitions(clock);

        Assert.Equal(SessionStatus.Completed, completed.Status);
        Assert.Equal(SessionStatus.Expired, expired.Status);
    }

    [Fact]
    public void FromPollFinalization_LinksTheOriginPoll_D160()
    {
        FakeClock clock = TestSessions.Clock();
        PollId pollId = PollId.New();

        Session session = Session.FromPollFinalization(
            pollId, UserId.New(), PastOption(TestSessions.DefaultStart), [], SessionStatus.Unconfirmed, clock);

        Assert.Equal(pollId, session.OriginPollId);
    }

    [Fact]
    public void FromPollFinalization_RejectsADuplicateParticipant_D119()
    {
        FakeClock clock = TestSessions.Clock();
        ParticipantRef twice = ParticipantRef.ForUser(UserId.New());

        DomainException error = Assert.Throws<DomainException>(
            () => Session.FromPollFinalization(
                PollId.New(),
                UserId.New(),
                PastOption(TestSessions.DefaultStart),
                [
                    new FinalizedParticipation(twice, ParticipationStatus.Going, false),
                    new FinalizedParticipation(twice, ParticipationStatus.Maybe, false),
                ],
                SessionStatus.Unconfirmed,
                clock));

        Assert.Equal(DomainError.Duplicate, error.Error);
    }

    [Fact]
    public void FromPollFinalization_MapsGuestVotersToo_D051()
    {
        FakeClock clock = TestSessions.Clock();
        GuestIdentityId guestId = GuestIdentityId.New();

        Session session = Session.FromPollFinalization(
            PollId.New(),
            UserId.New(),
            PastOption(TestSessions.DefaultStart, minAttendance: 1),
            [new FinalizedParticipation(ParticipantRef.ForGuest(guestId), ParticipationStatus.Going, false)],
            SessionStatus.Confirmed,
            clock);

        Participation row = session.Find(ParticipantRef.ForGuest(guestId))!;
        Assert.True(row.IsGuest);
        Assert.Equal(2, session.ActiveGoingCount);
    }
}
