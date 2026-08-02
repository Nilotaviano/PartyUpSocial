using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Users;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Domain.Tests.Lifecycle;

public class DeletionExecutorTests
{
    [Fact]
    public void Execute_CancelsOwnedNotStartedSessions_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.Equal(world.OwnedFuture.Id, Assert.Single(result.CanceledSessions));
        Assert.Equal(SessionStatus.Canceled, world.OwnedFuture.Status);
    }

    [Fact]
    public void Execute_CancelsTheOrganizersOwnSessionOnceWithoutUnconfirmingItFirst_D092()
    {
        DeletionWorld world = DeletionWorld.Build();
        Assert.Equal(SessionStatus.Confirmed, world.OwnedFuture.Status);

        world.Execute();

        IReadOnlyList<IDomainEvent> raised = world.OwnedFuture.DequeueEvents();
        Assert.Single(raised.OfType<SessionCanceled>());
        Assert.Empty(raised.OfType<SessionUnconfirmed>());
        Assert.Equal(ParticipationStatus.Going, LifecycleFixtures.RowOf(world.OwnedFuture, world.Subject.Id).Status);
    }

    [Fact]
    public void Execute_RevokesTheGuestLinksOfACanceledOwnedSession_D174()
    {
        DeletionWorld world = DeletionWorld.Build();

        world.Execute();

        Assert.Equal(GuestLinkState.RevokedByCancel, world.SessionGuestLink.State);
    }

    [Fact]
    public void Execute_LeavesAnOwnedInProgressSessionRunning_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.Equal(SessionStatus.Confirmed, world.OwnedInProgress.Status);
        Assert.DoesNotContain(world.OwnedInProgress.Id, result.CanceledSessions);
    }

    [Fact]
    public void Execute_LeavesAnOwnedCompletedSessionInHistory_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        world.Execute();

        Assert.Equal(SessionStatus.Completed, world.OwnedCompleted.Status);
        Assert.True(world.OwnedCompleted.IsRetainedInHistory);
    }

    [Fact]
    public void Execute_ExpiresOwnedPollingPolls_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.Equal(world.OwnedPoll.Id, Assert.Single(result.ExpiredPolls));
        Assert.Equal(PollStatus.Expired, world.OwnedPoll.Status);
        Assert.Equal(GuestLinkState.ExpiredPoll, world.PollGuestLink.State);
    }

    [Fact]
    public void Execute_NotifiesTheParticipantsOfAnExpiredOwnedPoll_D170()
    {
        DeletionWorld world = DeletionWorld.Build();

        world.Execute();

        Assert.Single(world.OwnedPoll.DequeueEvents().OfType<PollExpired>());
    }

    [Fact]
    public void Execute_RemovesSelfFromOtherPeoplesNotStartedSessions_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.Contains(world.OthersFuture.Id, result.RemovedFromSessions);
        Assert.Equal(
            ParticipationStatus.Removed,
            LifecycleFixtures.RowOf(world.OthersFuture, world.Subject.Id).Status);
    }

    [Fact]
    public void Execute_ReevaluatesConfirmationAfterRemovingSelf_D074()
    {
        DeletionWorld world = DeletionWorld.Build();
        Assert.Equal(SessionStatus.Confirmed, world.OthersFuture.Status);

        world.Execute();

        Assert.Equal(SessionStatus.Unconfirmed, world.OthersFuture.Status);
        Assert.Single(world.OthersFuture.DequeueEvents().OfType<SessionUnconfirmed>());
    }

    [Fact]
    public void Execute_CancelsThePendingRemindersOfEveryRemovedParticipation_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.All(result.CanceledReminders, reminder => Assert.Equal(world.Subject.Id, reminder.UserId));
        Assert.Contains(result.CanceledReminders, reminder => reminder.SessionId == world.OthersFuture.Id);
        Assert.Equal(result.RemovedFromSessions.Count, result.CanceledReminders.Count);
    }

    [Fact]
    public void Execute_RemovesAWaitlistedParticipationWithoutFreeingASlot_D092()
    {
        DeletionWorld world = DeletionWorld.Build();
        Assert.True(LifecycleFixtures.RowOf(world.OthersFull, world.Subject.Id).IsWaitlisted);

        DeletionExecutionResult result = world.Execute();

        Assert.Contains(world.OthersFull.Id, result.RemovedFromSessions);
        Assert.Equal(ParticipationStatus.Removed, LifecycleFixtures.RowOf(world.OthersFull, world.Subject.Id).Status);
        Assert.Equal(SessionStatus.Confirmed, world.OthersFull.Status);
        Assert.Empty(world.OthersFull.DequeueEvents().OfType<SessionWaitlistVacancy>());
    }

    [Fact]
    public void Execute_LeavesAParticipationInSomeoneElsesInProgressSessionAlone_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.DoesNotContain(world.OthersInProgress.Id, result.RemovedFromSessions);
        Assert.Equal(
            ParticipationStatus.Going,
            LifecycleFixtures.RowOf(world.OthersInProgress, world.Subject.Id).Status);
    }

    [Fact]
    public void Execute_LapsesTheDeletedUsersOwnOpenJoinRequests_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.Equal(world.OwnJoinRequest.Id, Assert.Single(result.LapsedJoinRequests));
        Assert.Equal(JoinRequestStatus.Lapsed, world.OwnJoinRequest.Status);
    }

    [Fact]
    public void Execute_LeavesAThirdPartysJoinRequestPending_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        world.Execute();

        Assert.True(world.StrangerJoinRequest.IsPending);
    }

    [Fact]
    public void Execute_RemovesSelfFromOtherPeoplesLivePolls_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.Equal(world.OthersPoll.Id, Assert.Single(result.RemovedFromPolls));
        Assert.True(LifecycleFixtures.RowOf(world.OthersPoll, world.Subject.Id).IsRemoved);
    }

    [Fact]
    public void Execute_ExcludesTheDeletedVoterFromALaterFinalization_D092()
    {
        DeletionWorld world = DeletionWorld.Build();
        world.Execute();

        PollFinalizationResult finalized = world.OthersPoll.Finalize(
            world.OthersPoll.Options[0].Id,
            LifecycleFixtures.At(LifecycleFixtures.GraceEnd + Duration.FromHours(1)));

        Assert.Null(finalized.Session.Find(ParticipantRef.ForUser(world.Subject.Id)));
        Assert.NotNull(finalized.Session.Find(ParticipantRef.ForUser(world.OthersPoll.OrganizerId)));
    }

    [Fact]
    public void Execute_DeletesEveryFriendshipAndBlockInBothDirections_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.Same(world.OwnFriendship, Assert.Single(result.DeletedFriendships));
        Assert.Same(world.BlockAgainstSubject, Assert.Single(result.DeletedBlocks));
    }

    [Fact]
    public void Execute_MarksTheAncillaryUserDataForDeletion_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.Equal(
            [
                UserDataScope.Availability,
                UserDataScope.NotificationMutes,
                UserDataScope.PushSubscriptions,
                UserDataScope.Notifications,
            ],
            result.PurgedData);
    }

    [Fact]
    public void Execute_TombstonesTheUser_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        world.Execute();

        Assert.True(world.Subject.IsTombstoned);
        Assert.Null(world.Subject.Username);
        Assert.Null(world.Subject.FriendCode);
        Assert.Equal(LifecycleFixtures.GraceEnd, world.Subject.DeletedAt);
    }

    [Fact]
    public void Execute_RetiresTheUsernameIntoAnOwnerlessReservation_D145()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.Same(world.UsernameEntry, result.ReservedUsername);
        Assert.True(world.UsernameEntry.IsReserved);
        Assert.Null(world.UsernameEntry.OwnerId);
        Assert.Equal(
            LifecycleFixtures.GraceEnd + UsernameRegistry.ReservationPeriod,
            world.UsernameEntry.ReservedUntil);
    }

    [Fact]
    public void Execute_AppendsThePseudonymousJournalEntry_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.Equal(new DeletionJournalEntry(world.Subject.Id, LifecycleFixtures.GraceEnd), result.Journal);
    }

    [Fact]
    public void Execute_TouchesEveryClauseOfTheDeletionWalk_D092()
    {
        DeletionWorld world = DeletionWorld.Build();

        DeletionExecutionResult result = world.Execute();

        Assert.Equal([world.OwnedFuture.Id], result.CanceledSessions);
        Assert.Equal([world.OwnedPoll.Id], result.ExpiredPolls);
        Assert.Equal([world.OthersFuture.Id, world.OthersFull.Id], result.RemovedFromSessions);
        Assert.Equal([world.OthersPoll.Id], result.RemovedFromPolls);
        Assert.Equal([world.OwnJoinRequest.Id], result.LapsedJoinRequests);
        Assert.Single(result.DeletedFriendships);
        Assert.Single(result.DeletedBlocks);
        Assert.NotNull(result.ReservedUsername);
        Assert.True(world.Subject.IsTombstoned);
    }

    [Fact]
    public void Execute_IsRejectedBeforeTheGraceEnds_D148()
    {
        DeletionWorld world = DeletionWorld.Build();
        FakeClock tooEarly = LifecycleFixtures.At(LifecycleFixtures.GraceEnd - Duration.FromNanoseconds(1L));

        DomainException error = Assert.Throws<DomainException>(
            () => DeletionExecutor.Execute(world.Subject, world.Scope, tooEarly));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void Execute_LeavesTheDomainUntouchedDuringTheGrace_D148()
    {
        DeletionWorld world = DeletionWorld.Build();
        FakeClock tooEarly = LifecycleFixtures.At(LifecycleFixtures.GraceEnd - Duration.FromNanoseconds(1L));

        Assert.Throws<DomainException>(() => DeletionExecutor.Execute(world.Subject, world.Scope, tooEarly));

        Assert.Equal(SessionStatus.Confirmed, world.OwnedFuture.Status);
        Assert.Equal(PollStatus.Polling, world.OwnedPoll.Status);
        Assert.True(world.OwnJoinRequest.IsPending);
        Assert.Equal(ParticipationStatus.Going, LifecycleFixtures.RowOf(world.OthersFuture, world.Subject.Id).Status);
        Assert.True(world.Subject.IsPendingDeletion);
    }

    [Fact]
    public void Execute_IsRejectedForAUserWhoNeverRequestedDeletion_D148()
    {
        FakeClock clock = LifecycleFixtures.Clock();
        User active = TestUsers.Active(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => DeletionExecutor.Execute(active, DeletionScope.Empty, clock));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }

    [Fact]
    public void Execute_RunTwiceLeavesTheSameEndState_D092()
    {
        DeletionWorld world = DeletionWorld.Build();
        DeletionExecutionResult first = world.Execute();
        world.DrainEvents();

        DeletionExecutionResult second = world.Execute();

        Assert.Empty(second.CanceledSessions);
        Assert.Empty(second.ExpiredPolls);
        Assert.Empty(second.RemovedFromSessions);
        Assert.Empty(second.RemovedFromPolls);
        Assert.Empty(second.LapsedJoinRequests);
        Assert.Empty(second.CanceledReminders);
        Assert.Null(second.ReservedUsername);
        Assert.Equal(first.Journal, second.Journal);
        Assert.Equal(SessionStatus.Canceled, world.OwnedFuture.Status);
        Assert.Equal(SessionStatus.Confirmed, world.OwnedInProgress.Status);
        Assert.Equal(PollStatus.Expired, world.OwnedPoll.Status);
        Assert.Empty(world.DrainEvents());
    }

    /// <summary>
    /// The D-092 clause walk in one world: the deleting user simultaneously organizes a future, an
    /// in-progress and a Completed session, participates in and is waitlisted on other people's
    /// content, holds an open join request, owns guest links, owns a live poll and has voted in
    /// someone else's poll that is finalized only after the execution.
    /// </summary>
    private sealed record DeletionWorld(
        User Subject,
        Session OwnedFuture,
        Session OwnedInProgress,
        Session OwnedCompleted,
        Session OthersFuture,
        Session OthersFull,
        Session OthersInProgress,
        Session OthersRequested,
        JoinRequest OwnJoinRequest,
        JoinRequest StrangerJoinRequest,
        Poll OwnedPoll,
        Poll OthersPoll,
        GuestLink SessionGuestLink,
        GuestLink PollGuestLink,
        Friendship OwnFriendship,
        Friendship UnrelatedFriendship,
        Block BlockAgainstSubject,
        Block UnrelatedBlock,
        UsernameRegistryEntry UsernameEntry)
    {
        internal DeletionScope Scope => new(
            [OwnedFuture, OwnedInProgress, OwnedCompleted, OthersFuture, OthersFull, OthersInProgress, OthersRequested],
            [OwnedPoll, OthersPoll],
            [OwnFriendship, UnrelatedFriendship],
            [BlockAgainstSubject, UnrelatedBlock],
            UsernameEntry);

        internal static DeletionWorld Build()
        {
            FakeClock clock = LifecycleFixtures.Clock();
            User subject = TestUsers.PendingDeletion(clock);
            UserId me = subject.Id;
            ParticipantRef meRef = ParticipantRef.ForUser(me);

            UserId friend = UserId.New();
            UserId filler = UserId.New();
            UserId stranger = UserId.New();
            Instant inProgressStart = LifecycleFixtures.GraceEnd - Duration.FromHours(1);

            Session ownedFuture = LifecycleFixtures.NewSession(me, [friend], LifecycleFixtures.Days(10), clock);
            ownedFuture.Rsvp(ParticipantRef.ForUser(friend), ParticipationStatus.Going, clock);
            (GuestLink sessionLink, _) = ownedFuture.CreateGuestLink(clock);
            ownedFuture.ClaimGuestLink(
                sessionLink.Id, DisplayName.Create("Ada"), ParticipationStatus.Going, clock);

            Session ownedInProgress = LifecycleFixtures.NewSession(
                me, [friend], inProgressStart, clock, minAttendance: 1);

            Session ownedCompleted = LifecycleFixtures.NewSession(
                me, [friend], LifecycleFixtures.Days(1), clock, minAttendance: 1);
            ownedCompleted.ApplyDueTransitions(
                LifecycleFixtures.At(LifecycleFixtures.Days(1) + Duration.FromHours(3)));

            Session othersFuture = LifecycleFixtures.NewSession(
                UserId.New(), [me], LifecycleFixtures.Days(10), clock);
            othersFuture.Rsvp(meRef, ParticipationStatus.Going, clock);

            Session othersFull = LifecycleFixtures.NewSession(
                UserId.New(), [filler, me], LifecycleFixtures.Days(10), clock, minAttendance: 2, maxCapacity: 2);
            othersFull.Rsvp(ParticipantRef.ForUser(filler), ParticipationStatus.Going, clock);
            othersFull.Rsvp(meRef, ParticipationStatus.Going, clock);

            Session othersInProgress = LifecycleFixtures.NewSession(
                UserId.New(), [me], inProgressStart, clock, minAttendance: 1);
            othersInProgress.Rsvp(meRef, ParticipationStatus.Going, clock);

            Session othersRequested = LifecycleFixtures.NewSession(
                UserId.New(), [friend], LifecycleFixtures.Days(10), clock, minAttendance: 1);
            JoinRequest ownRequest = othersRequested.OpenJoinRequest(me, clock);
            JoinRequest strangerRequest = othersRequested.OpenJoinRequest(stranger, clock);

            Poll ownedPoll = LifecycleFixtures.NewPoll(
                me, [friend], [LifecycleFixtures.Days(20), LifecycleFixtures.Days(21)], clock);
            (GuestLink pollLink, _) = ownedPoll.CreateGuestLink(clock);

            // Created late enough that its 48-hour selection window is still open at the execution,
            // so a finalization can be attempted afterwards (D-092: the deleted user must not map in).
            FakeClock sixDaysIn = LifecycleFixtures.At(LifecycleFixtures.Days(6));
            Poll othersPoll = LifecycleFixtures.NewPoll(
                UserId.New(),
                [me],
                [LifecycleFixtures.Days(20), LifecycleFixtures.Days(21)],
                sixDaysIn,
                minAttendance: 1);
            othersPoll.Vote(
                meRef,
                othersPoll.Options[0].Id,
                VoteChoice.Available,
                LifecycleFixtures.At(LifecycleFixtures.Days(6) + Duration.FromHours(1)));

            User buddy = TestUsers.Active(clock, "buddy", "Buddy", "PU-BUDDY1");
            Friendship ownFriendship = Friendship.Request(subject, buddy, clock);
            ownFriendship.Accept(buddy.Id, clock);

            User first = TestUsers.Active(clock, "firstie", "First", "PU-FIRST1");
            User second = TestUsers.Active(clock, "secondie", "Second", "PU-SECND1");
            Friendship unrelated = Friendship.Request(first, second, clock);

            Block blockAgainstSubject = Block.Create(
                stranger,
                me,
                subject.Username!.Value,
                clock);
            Block unrelatedBlock = Block.Create(
                first.Id,
                second.Id,
                second.Username!.Value,
                clock);

            UsernameRegistryEntry usernameEntry = UsernameRegistry.Claim(
                null, subject.Username!.Value, me, clock);

            DeletionWorld world = new(
                subject,
                ownedFuture,
                ownedInProgress,
                ownedCompleted,
                othersFuture,
                othersFull,
                othersInProgress,
                othersRequested,
                ownRequest,
                strangerRequest,
                ownedPoll,
                othersPoll,
                sessionLink,
                pollLink,
                ownFriendship,
                unrelated,
                blockAgainstSubject,
                unrelatedBlock,
                usernameEntry);

            world.DrainEvents();
            return world;
        }

        internal DeletionExecutionResult Execute() =>
            DeletionExecutor.Execute(Subject, Scope, LifecycleFixtures.At(LifecycleFixtures.GraceEnd));

        internal List<IDomainEvent> DrainEvents()
        {
            List<IDomainEvent> drained = [];

            foreach (Session session in Scope.Sessions)
            {
                drained.AddRange(session.DequeueEvents());
            }

            foreach (Poll poll in Scope.Polls)
            {
                drained.AddRange(poll.DequeueEvents());
            }

            drained.AddRange(Subject.DequeueEvents());
            return drained;
        }
    }
}
