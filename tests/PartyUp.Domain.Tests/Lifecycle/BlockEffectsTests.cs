using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Lifecycle;

public class BlockEffectsTests
{
    private static readonly UserId Blocker = UserId.New();
    private static readonly UserId Blocked = UserId.New();
    private static readonly UserId Third = UserId.New();

    private static ParticipantRef BlockerRef => ParticipantRef.ForUser(Blocker);

    private static ParticipantRef BlockedRef => ParticipantRef.ForUser(Blocked);

    [Fact]
    public void Apply_DeletesTheFriendshipBetweenThePairInEitherStatus_D065()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Friendship pending = FriendshipBetween(Blocker, Blocked, clock, accept: false);
        Friendship accepted = FriendshipBetween(Blocked, Third, clock, accept: true);
        Block block = CreateBlock(clock);

        BlockEffectResult result = BlockEffects.Apply(block, [pending, accepted], [], [], clock);

        Assert.Same(pending, Assert.Single(result.DeletedFriendships));
    }

    [Fact]
    public void Apply_DeletesTheAcceptedFriendshipToo_D065()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Friendship accepted = FriendshipBetween(Blocker, Blocked, clock, accept: true);
        Block block = CreateBlock(clock);

        BlockEffectResult result = BlockEffects.Apply(block, [accepted], [], [], clock);

        Assert.Same(accepted, Assert.Single(result.DeletedFriendships));
    }

    [Fact]
    public void Apply_RemovesTheParticipantFromTheBlockersSession_D077()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        session.Rsvp(BlockedRef, ParticipationStatus.Going, clock);
        session.DequeueEvents();
        Block block = CreateBlock(clock);

        BlockEffectResult result = BlockEffects.Apply(block, [], [session], [], clock);

        Assert.Equal(ParticipationStatus.Removed, LifecycleFixtures.RowOf(session, Blocked).Status);
        Assert.Equal(session.Id, Assert.Single(result.RemovedFromSessions));
        Assert.Equal(1, session.ActiveOccupancy);
    }

    [Fact]
    public void Apply_RemovesTheBlockerFromTheBlockedUsersSession_D077()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocked, [Blocker], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        session.Rsvp(BlockerRef, ParticipationStatus.Going, clock);
        session.DequeueEvents();
        Block block = CreateBlock(clock);

        BlockEffectResult result = BlockEffects.Apply(block, [], [session], [], clock);

        Assert.Equal(ParticipationStatus.Removed, LifecycleFixtures.RowOf(session, Blocker).Status);
        Assert.Equal(session.Id, Assert.Single(result.RemovedFromSessions));
    }

    [Fact]
    public void Apply_RemovesADeclinedInviteeSoTheRetainedInvitedAccessIsCut_D077()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        session.DeclineInvitation(BlockedRef, clock);
        session.DequeueEvents();
        Block block = CreateBlock(clock);

        BlockEffects.Apply(block, [], [session], [], clock);

        Assert.Equal(ParticipationStatus.Removed, LifecycleFixtures.RowOf(session, Blocked).Status);
    }

    [Fact]
    public void Apply_RemovesAPendingInviteeSilently_D105()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        session.DequeueEvents();
        Block block = CreateBlock(clock);

        BlockEffects.Apply(block, [], [session], [], clock);

        Assert.Equal(ParticipationStatus.Removed, LifecycleFixtures.RowOf(session, Blocked).Status);
        Assert.Empty(session.DequeueEvents());
    }

    [Fact]
    public void Apply_UnconfirmsWhenTheRemovalDropsActiveGoingBelowTheMinimum_D074()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 2);
        session.Rsvp(BlockedRef, ParticipationStatus.Going, clock);
        Assert.Equal(SessionStatus.Confirmed, session.Status);
        session.DequeueEvents();
        Block block = CreateBlock(clock);

        BlockEffects.Apply(block, [], [session], [], clock);

        Assert.Equal(SessionStatus.Unconfirmed, session.Status);
        Assert.Single(session.DequeueEvents().OfType<SessionUnconfirmed>());
    }

    [Fact]
    public void Apply_KeepsConfirmationWhileAGoingWaitlistedCandidateRemains_D074()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(
            Blocker,
            [Blocked, Third],
            LifecycleFixtures.Hours(24),
            clock,
            minAttendance: 2,
            maxCapacity: 2);
        session.Rsvp(BlockedRef, ParticipationStatus.Going, clock);
        session.Rsvp(ParticipantRef.ForUser(Third), ParticipationStatus.Going, clock);
        Assert.True(LifecycleFixtures.RowOf(session, Third).IsWaitlisted);
        Assert.Equal(SessionStatus.Confirmed, session.Status);
        session.DequeueEvents();
        Block block = CreateBlock(clock);

        BlockEffects.Apply(block, [], [session], [], clock);

        Assert.Equal(SessionStatus.Confirmed, session.Status);
        Assert.Single(session.DequeueEvents().OfType<SessionWaitlistVacancy>());
    }

    [Fact]
    public void Apply_CancelsThePendingReminderOfTheRemovedParticipant_D077()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        session.Rsvp(BlockedRef, ParticipationStatus.Going, clock);
        Block block = CreateBlock(clock);

        BlockEffectResult result = BlockEffects.Apply(block, [], [session], [], clock);

        SessionReminderCanceled canceled = Assert.Single(result.CanceledReminders);
        Assert.Equal(session.Id, canceled.SessionId);
        Assert.Equal(Blocked, canceled.UserId);
    }

    [Fact]
    public void Apply_RemovesFromAnInProgressSession_D077()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        session.Rsvp(BlockedRef, ParticipationStatus.Going, clock);
        FakeClock inProgress = LifecycleFixtures.At(LifecycleFixtures.Hours(25));
        Block block = CreateBlock(inProgress);

        BlockEffectResult result = BlockEffects.Apply(block, [], [session], [], inProgress);

        Assert.Equal(ParticipationStatus.Removed, LifecycleFixtures.RowOf(session, Blocked).Status);
        Assert.Single(result.RemovedFromSessions);
    }

    [Fact]
    public void Apply_LeavesACompletedRosterFrozen_D095()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        session.Rsvp(BlockedRef, ParticipationStatus.Going, clock);
        FakeClock after = LifecycleFixtures.At(LifecycleFixtures.Hours(30));
        session.ApplyDueTransitions(after);
        Assert.Equal(SessionStatus.Completed, session.Status);
        Block block = CreateBlock(after);

        BlockEffectResult result = BlockEffects.Apply(block, [], [session], [], after);

        Assert.Equal(ParticipationStatus.Going, LifecycleFixtures.RowOf(session, Blocked).Status);
        Assert.Empty(result.RemovedFromSessions);
    }

    [Fact]
    public void Apply_LeavesAnEndedButUnsweptSessionAlone_D095()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        session.Rsvp(BlockedRef, ParticipationStatus.Going, clock);
        FakeClock after = LifecycleFixtures.At(LifecycleFixtures.Hours(30));
        Block block = CreateBlock(after);

        BlockEffectResult result = BlockEffects.Apply(block, [], [session], [], after);

        Assert.Equal(ParticipationStatus.Going, LifecycleFixtures.RowOf(session, Blocked).Status);
        Assert.Empty(result.RemovedFromSessions);
    }

    [Fact]
    public void Apply_LeavesAThirdPartySessionUntouchedSoTheyCoexistMasked_D076()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Third, [Blocker, Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        session.Rsvp(BlockerRef, ParticipationStatus.Going, clock);
        session.Rsvp(BlockedRef, ParticipationStatus.Going, clock);
        Block block = CreateBlock(clock);

        BlockEffectResult result = BlockEffects.Apply(block, [], [session], [], clock);

        Assert.Equal(ParticipationStatus.Going, LifecycleFixtures.RowOf(session, Blocker).Status);
        Assert.Equal(ParticipationStatus.Going, LifecycleFixtures.RowOf(session, Blocked).Status);
        Assert.Empty(result.RemovedFromSessions);
    }

    [Fact]
    public void Apply_LapsesAnOpenJoinRequestBetweenThePair_D065()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Third], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        JoinRequest request = session.OpenJoinRequest(Blocked, clock);
        Block block = CreateBlock(clock);

        BlockEffectResult result = BlockEffects.Apply(block, [], [session], [], clock);

        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
        Assert.Equal(request.Id, Assert.Single(result.LapsedJoinRequests));
    }

    [Fact]
    public void Apply_LeavesAThirdPartysJoinRequestPending_D065()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        JoinRequest request = session.OpenJoinRequest(Third, clock);
        Block block = CreateBlock(clock);

        BlockEffectResult result = BlockEffects.Apply(block, [], [session], [], clock);

        Assert.True(request.IsPending);
        Assert.Empty(result.LapsedJoinRequests);
    }

    [Fact]
    public void Apply_LapsesAnOpenPollJoinRequestBetweenThePair_D065()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Poll poll = LifecycleFixtures.NewPoll(Blocker, [Third], [LifecycleFixtures.Hours(72), LifecycleFixtures.Hours(96)], clock);
        JoinRequest request = poll.OpenJoinRequest(Blocked, clock);
        Block block = CreateBlock(clock);

        BlockEffectResult result = BlockEffects.Apply(block, [], [], [poll], clock);

        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
        Assert.Equal(request.Id, Assert.Single(result.LapsedJoinRequests));
    }

    [Fact]
    public void Apply_RemovesThePollParticipantOnTheOrganizersPoll_D077()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Poll poll = LifecycleFixtures.NewPoll(Blocker, [Blocked], [LifecycleFixtures.Hours(72), LifecycleFixtures.Hours(96)], clock);
        poll.Vote(BlockedRef, poll.Options[0].Id, VoteChoice.Available, clock);
        Block block = CreateBlock(clock);

        BlockEffectResult result = BlockEffects.Apply(block, [], [], [poll], clock);

        Assert.True(LifecycleFixtures.RowOf(poll, Blocked).IsRemoved);
        Assert.Equal(poll.Id, Assert.Single(result.RemovedFromPolls));
    }

    [Fact]
    public void Apply_ExcludesTheRemovedVoterFromALaterFinalization_D077()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Poll poll = LifecycleFixtures.NewPoll(
            Blocker,
            [Blocked],
            [LifecycleFixtures.Hours(72), LifecycleFixtures.Hours(96)],
            clock,
            minAttendance: 1);
        poll.Vote(BlockedRef, poll.Options[0].Id, VoteChoice.Available, clock);
        Block block = CreateBlock(clock);
        BlockEffects.Apply(block, [], [], [poll], clock);

        PollFinalizationResult finalized = poll.Finalize(poll.Options[0].Id, LifecycleFixtures.At(LifecycleFixtures.Hours(25)));

        Assert.Null(finalized.Session.Find(BlockedRef));
        Assert.NotNull(finalized.Session.Find(BlockerRef));
    }

    [Fact]
    public void Apply_LeavesAFinalizedPollAlone_D077()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Poll poll = LifecycleFixtures.NewPoll(
            Blocker,
            [Blocked],
            [LifecycleFixtures.Hours(72), LifecycleFixtures.Hours(96)],
            clock,
            minAttendance: 1);
        poll.Vote(BlockedRef, poll.Options[0].Id, VoteChoice.Available, clock);
        FakeClock atSelection = LifecycleFixtures.At(LifecycleFixtures.Hours(25));
        poll.Finalize(poll.Options[0].Id, atSelection);
        Block block = CreateBlock(atSelection);

        BlockEffectResult result = BlockEffects.Apply(block, [], [], [poll], atSelection);

        Assert.True(LifecycleFixtures.RowOf(poll, Blocked).IsActive);
        Assert.Empty(result.RemovedFromPolls);
    }

    [Fact]
    public void Apply_IsIdempotentWhenReRun_D077()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        session.Rsvp(BlockedRef, ParticipationStatus.Going, clock);
        Poll poll = LifecycleFixtures.NewPoll(Blocker, [Blocked], [LifecycleFixtures.Hours(72), LifecycleFixtures.Hours(96)], clock);
        JoinRequest request = poll.OpenJoinRequest(Third, clock);
        Block block = CreateBlock(clock);

        BlockEffects.Apply(block, [], [session], [poll], clock);
        session.DequeueEvents();
        poll.DequeueEvents();

        BlockEffectResult second = BlockEffects.Apply(block, [], [session], [poll], clock);

        Assert.Empty(second.RemovedFromSessions);
        Assert.Empty(second.RemovedFromPolls);
        Assert.Empty(second.LapsedJoinRequests);
        Assert.Empty(second.CanceledReminders);
        Assert.Empty(session.DequeueEvents());
        Assert.Empty(poll.DequeueEvents());
        Assert.True(request.IsPending);
    }

    [Fact]
    public void Unblock_RestoresNothing_D065()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        session.Rsvp(BlockedRef, ParticipationStatus.Going, clock);
        Friendship friendship = FriendshipBetween(Blocker, Blocked, clock, accept: true);
        Block block = CreateBlock(clock);
        BlockEffects.Apply(block, [friendship], [session], [], clock);
        session.DequeueEvents();

        BlockEffectResult result = BlockEffects.Unblock(block);

        Assert.Same(BlockEffectResult.None, result);
        Assert.Empty(result.DeletedFriendships);
        Assert.Equal(ParticipationStatus.Removed, LifecycleFixtures.RowOf(session, Blocked).Status);
        Assert.Empty(session.DequeueEvents());
    }

    [Fact]
    public void Unblock_LeavesTheRemovalPermanentForThatSession_D077()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        Session session = LifecycleFixtures.NewSession(Blocker, [Blocked], LifecycleFixtures.Hours(24), clock, minAttendance: 1);
        Block block = CreateBlock(clock);
        BlockEffects.Apply(block, [], [session], [], clock);
        BlockEffects.Unblock(block);

        DomainException error = Assert.Throws<DomainException>(() => session.Invite(Blocked, clock));

        Assert.Equal(DomainError.RemovalIsPermanent, error.Error);
    }

    private static Friendship FriendshipBetween(UserId first, UserId second, IClock clock, bool accept)
    {
        Friendship friendship = Friendship.Request(TestUser(first, clock), TestUser(second, clock), clock);

        if (accept)
        {
            friendship.Accept(second, clock);
        }

        return friendship;
    }

    private static Block CreateBlock(IClock clock) =>
        Block.Create(Blocker, Blocked, Username.Create("blocked-user"), clock);

    private static User TestUser(UserId id, IClock clock)
    {
        User user = User.BeginOnboarding(id, FriendCode.Create("PU-ABC123"), AgeGateSource.SelfAttested, clock);

        user.CompleteOnboarding(
            Username.Create("player"),
            DisplayName.Create("Player"),
            Avatar.FromEmoji("\U0001F642"),
            DateTimeZoneProviders.Tzdb["America/Sao_Paulo"],
            Locale.Create("en"),
            clock);

        return user;
    }
}
