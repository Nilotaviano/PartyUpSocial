using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Polls;

public class PollFinalizationTests
{
    /// <summary>Inside the selection window [Deadline, Deadline + 48 h) for the default 24-hour poll.</summary>
    private static FakeClock SelectionTime => TestPolls.At(TestPolls.Hours(25));

    [Fact]
    public void Finalize_CopiesTheAudienceOntoTheMintedSession_D204()
    {
        FakeClock clock = TestPolls.Clock();
        FriendGroupId groupA = FriendGroupId.New();
        FriendGroupId groupB = FriendGroupId.New();
        Poll poll = Poll.Create(
            UserId.New(),
            TestPolls.Details(minAttendance: 1, visibility: ContentVisibility.Groups),
            PollDeadlineChoice.Hours24,
            [TestPolls.Hours(72), TestPolls.Hours(96)],
            [],
            clock,
            audienceGroupIds: [groupA, groupB]);

        Session session = poll.Finalize(poll.Options[0].Id, SelectionTime).Session;

        Assert.Equal(ContentVisibility.Groups, session.Visibility);
        Assert.Equal(
            new HashSet<FriendGroupId> { groupA, groupB },
            new HashSet<FriendGroupId>(session.AudienceGroupIds));
    }

    [Fact]
    public void Finalize_MapsVotesOnTheSelectedOption_D051()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 4, minAttendance: 1);
        PollOptionId chosen = fixture.Option(0).Id;

        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Maybe, clock);
        fixture.Poll.Vote(fixture.InviteeRef(2), chosen, VoteChoice.Unavailable, clock);
        fixture.Poll.Vote(fixture.InviteeRef(3), fixture.Option(1).Id, VoteChoice.Available, clock);

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Assert.Equal(ParticipationStatus.Going, session.Find(fixture.InviteeRef(0))!.Status);
        Assert.Equal(ParticipationStatus.Maybe, session.Find(fixture.InviteeRef(1))!.Status);
        Assert.Equal(ParticipationStatus.Declined, session.Find(fixture.InviteeRef(2))!.Status);
        Assert.Equal(ParticipationStatus.Pending, session.Find(fixture.InviteeRef(3))!.Status);
        Assert.All(session.Participations, row => Assert.Equal(ParticipationOrigin.PollFinalization, row.Origin));
    }

    [Fact]
    public void Finalize_MakesTheOrganizerGoingRegardlessOfTheirVote_D155()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.OrganizerRef, chosen, VoteChoice.Unavailable, clock);

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Participation organizer = session.Find(fixture.OrganizerRef)!;
        Assert.Equal(ParticipationStatus.Going, organizer.Status);
        Assert.False(organizer.IsWaitlisted);
    }

    [Fact]
    public void Finalize_ExcludesRemovedParticipantsFromTheMapping_D092()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);
        fixture.Poll.RemoveParticipant(fixture.InviteeRef(0), clock);

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Assert.Null(session.Find(fixture.InviteeRef(0)));
        Assert.Equal(1, session.ActiveGoingCount);
    }

    [Fact]
    public void Finalize_SetsTheSelectedOptionAsTheSessionStart_D153()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        PollOption chosen = fixture.Option(1);

        PollFinalizationResult result = fixture.Poll.Finalize(chosen.Id, SelectionTime);

        Assert.Equal(chosen.StartTime, result.Session.StartTime);
        Assert.Equal(chosen.StartTime, result.SelectedStartTime);
        Assert.Equal(fixture.Poll.Id, result.Session.OriginPollId);
        Assert.Equal(PollStatus.Finalized, fixture.Poll.Status);
        Assert.Equal(chosen.Id, fixture.Poll.SelectedOptionId);
        Assert.Equal(result.Session.Id, fixture.Poll.CreatedSessionId);
    }

    [Fact]
    public void Finalize_CarriesEverySessionDetailOntoTheSession_D154()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 3, maxCapacity: 6, durationMinutes: 90);

        Session session = fixture.Poll.Finalize(fixture.Option(0).Id, SelectionTime).Session;

        Assert.Equal(fixture.Poll.Title, session.Title);
        Assert.Equal(90, session.Duration.TotalMinutes);
        Assert.Equal(3, session.MinAttendance);
        Assert.Equal(6, session.MaxCapacity);
    }

    // ---- D-119 allocation ----------------------------------------------------------------------

    [Fact]
    public void Finalize_AllocatesOrganizerThenAvailableThenMaybeByResponseTime_D119()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 4, minAttendance: 1, maxCapacity: 3);
        PollOptionId chosen = fixture.Option(0).Id;

        clock.Advance(Duration.FromHours(1));
        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Available, clock);
        clock.Advance(Duration.FromHours(1));
        fixture.Poll.Vote(fixture.InviteeRef(2), chosen, VoteChoice.Maybe, clock);
        clock.Advance(Duration.FromHours(1));
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);
        clock.Advance(Duration.FromHours(1));
        fixture.Poll.Vote(fixture.InviteeRef(3), chosen, VoteChoice.Maybe, clock);

        PollFinalizationResult result = fixture.Poll.Finalize(chosen, SelectionTime);
        Session session = result.Session;

        Assert.False(session.Find(fixture.OrganizerRef)!.IsWaitlisted);
        Assert.False(session.Find(fixture.InviteeRef(1))!.IsWaitlisted);
        Assert.False(session.Find(fixture.InviteeRef(0))!.IsWaitlisted);
        Assert.True(session.Find(fixture.InviteeRef(2))!.IsWaitlisted);
        Assert.True(session.Find(fixture.InviteeRef(3))!.IsWaitlisted);
        Assert.Equal(3, session.ActiveOccupancy);
        Assert.Equal(2, result.Waitlisted.Count);
        Assert.Equal(fixture.InviteeRef(2), result.Waitlisted[0]);
        Assert.Equal(fixture.InviteeRef(3), result.Waitlisted[1]);
    }

    [Fact]
    public void Finalize_WaitlistsOverflowWithTheMatchingAttendanceStatus_D119()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 1);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Maybe, clock);

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Assert.Equal(ParticipationStatus.Going, session.Find(fixture.InviteeRef(0))!.Status);
        Assert.True(session.Find(fixture.InviteeRef(0))!.IsWaitlisted);
        Assert.Equal(ParticipationStatus.Maybe, session.Find(fixture.InviteeRef(1))!.Status);
        Assert.True(session.Find(fixture.InviteeRef(1))!.IsWaitlisted);
        Assert.Equal(1, session.ActiveOccupancy);
    }

    [Fact]
    public void Finalize_BreaksIdenticalResponseTimesByParticipantId_D119()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 2);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Available, clock);

        Assert.Equal(
            fixture.Row(fixture.InviteeRef(0)).FirstResponseAt,
            fixture.Row(fixture.InviteeRef(1)).FirstResponseAt);

        ParticipantRef lower = fixture.InviteeRef(0) < fixture.InviteeRef(1)
            ? fixture.InviteeRef(0)
            : fixture.InviteeRef(1);
        ParticipantRef higher = lower == fixture.InviteeRef(0) ? fixture.InviteeRef(1) : fixture.InviteeRef(0);

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Assert.False(session.Find(lower)!.IsWaitlisted);
        Assert.True(session.Find(higher)!.IsWaitlisted);
    }

    [Fact]
    public void Finalize_AllocatesTheOrganizerOnceEvenWhenTheyVotedAvailableEarliest_D119()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 2);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.OrganizerRef, chosen, VoteChoice.Available, clock);

        clock.Advance(Duration.FromHours(1));
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);
        clock.Advance(Duration.FromHours(1));
        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Available, clock);

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Assert.Equal(3, session.Participations.Count);
        Assert.Single(session.Participations, row => row.Matches(fixture.OrganizerRef));
        Assert.False(session.Find(fixture.OrganizerRef)!.IsWaitlisted);
        Assert.False(session.Find(fixture.InviteeRef(0))!.IsWaitlisted);
        Assert.True(session.Find(fixture.InviteeRef(1))!.IsWaitlisted);
        Assert.Equal(2, session.ActiveOccupancy);
    }

    [Fact]
    public void Finalize_WaitlistsTheOrganizerNever_D119()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 3, minAttendance: 1, maxCapacity: 1);
        PollOptionId chosen = fixture.Option(0).Id;

        foreach (int index in new[] { 0, 1, 2 })
        {
            fixture.Poll.Vote(fixture.InviteeRef(index), chosen, VoteChoice.Available, clock);
        }

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Assert.False(session.Find(fixture.OrganizerRef)!.IsWaitlisted);
        Assert.Equal(3, session.WaitlistCount);
    }

    [Fact]
    public void Finalize_WaitlistsNobodyWithoutAMaximum_D091()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 4, minAttendance: 1, maxCapacity: null);
        PollOptionId chosen = fixture.Option(0).Id;

        foreach (int index in new[] { 0, 1, 2, 3 })
        {
            fixture.Poll.Vote(fixture.InviteeRef(index), chosen, VoteChoice.Available, clock);
        }

        PollFinalizationResult result = fixture.Poll.Finalize(chosen, SelectionTime);

        Assert.Empty(result.Waitlisted);
        Assert.Equal(0, result.Session.WaitlistCount);
    }

    [Fact]
    public void Finalize_NeverWaitlistsDeclinedOrPendingRows_D119()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 1);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Unavailable, clock);

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Assert.False(session.Find(fixture.InviteeRef(0))!.IsWaitlisted);
        Assert.False(session.Find(fixture.InviteeRef(1))!.IsWaitlisted);
        Assert.Equal(0, session.WaitlistCount);
    }

    [Fact]
    public void Finalize_RaisesAWaitlistEventPerOverflowParticipant_D119()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 1);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Available, clock);
        fixture.Drain();

        fixture.Poll.Finalize(chosen, SelectionTime);

        Assert.Equal(2, fixture.Drain().OfType<PollWaitlistAdded>().Count());
    }

    // ---- D-153 branches ------------------------------------------------------------------------

    [Fact]
    public void Finalize_ConfirmsAFutureOptionThatMeetsTheMinimum_D153()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 2);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Assert.Equal(SessionStatus.Confirmed, session.Status);
    }

    [Fact]
    public void Finalize_LeavesAFutureOptionUnconfirmedBelowTheMinimum_D153()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 3);

        Session session = fixture.Poll.Finalize(fixture.Option(0).Id, SelectionTime).Session;

        Assert.Equal(SessionStatus.Unconfirmed, session.Status);
    }

    [Fact]
    public void Finalize_CreatesAnInProgressSessionFromAStartedOption_D153()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock,
            minAttendance: 1,
            durationMinutes: 120,
            optionStartTimes: [TestPolls.Hours(24), TestPolls.Hours(96)]);
        FakeClock inProgress = TestPolls.At(TestPolls.Hours(25));

        Session session = fixture.Poll.Finalize(fixture.Option(0).Id, inProgress).Session;

        Assert.Equal(SessionStatus.Confirmed, session.Status);
        Assert.True(session.IsInProgress(inProgress));
    }

    [Fact]
    public void Finalize_CompletesAnEndedOptionThatMetTheMinimum_D153()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock,
            minAttendance: 2,
            durationMinutes: 120,
            optionStartTimes: [TestPolls.Hours(20), TestPolls.Hours(96)]);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.True(session.IsRetainedInHistory);
    }

    [Fact]
    public void Finalize_ExpiresAnEndedOptionBelowTheMinimum_D164()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock,
            minAttendance: 3,
            durationMinutes: 120,
            optionStartTimes: [TestPolls.Hours(20), TestPolls.Hours(96)]);

        Session session = fixture.Poll.Finalize(fixture.Option(0).Id, SelectionTime).Session;

        Assert.Equal(SessionStatus.Expired, session.Status);
        Assert.False(session.IsRetainedInHistory);
    }

    [Fact]
    public void Finalize_AnnouncesTheSelectedOptionInEveryBranchIncludingExpired_D170()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock,
            minAttendance: 4,
            durationMinutes: 120,
            optionStartTimes: [TestPolls.Hours(20), TestPolls.Hours(96)]);
        fixture.Drain();

        PollFinalizationResult result = fixture.Poll.Finalize(fixture.Option(0).Id, SelectionTime);

        Assert.Equal(SessionStatus.Expired, result.Session.Status);
        PollOptionSelected selected = Assert.Single(fixture.Drain().OfType<PollOptionSelected>());
        Assert.Equal(fixture.Option(0).Id, selected.OptionId);
        Assert.Equal(result.Session.Id, selected.SessionId);
    }

    [Fact]
    public void Finalize_AnnouncesTheSelectedOptionOnACompletedBranch_D112()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock,
            minAttendance: 1,
            durationMinutes: 120,
            optionStartTimes: [TestPolls.Hours(20), TestPolls.Hours(96)]);
        fixture.Drain();

        fixture.Poll.Finalize(fixture.Option(0).Id, SelectionTime);

        Assert.Single(fixture.Drain().OfType<PollOptionSelected>());
    }

    [Fact]
    public void Finalize_LeavesNonVotersPendingOnAStartedSession_D081()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock,
            minAttendance: 1,
            durationMinutes: 120,
            optionStartTimes: [TestPolls.Hours(24), TestPolls.Hours(96)]);
        FakeClock inProgress = TestPolls.At(TestPolls.Hours(25));

        Session session = fixture.Poll.Finalize(fixture.Option(0).Id, inProgress).Session;

        Assert.Equal(ParticipationStatus.Pending, session.Find(fixture.InviteeRef(0))!.Status);

        DomainException error = Assert.Throws<DomainException>(
            () => session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, inProgress));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    // ---- clock gates ---------------------------------------------------------------------------

    [Fact]
    public void Finalize_IsRejectedBeforeTheDeadlineWhileNoOptionIsViable_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 3);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Finalize(fixture.Option(0).Id, TestPolls.At(TestPolls.DefaultDeadline - Duration.FromMinutes(1))));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
        Assert.Equal(PollStatus.Polling, fixture.Poll.Status);
    }

    [Fact]
    public void Finalize_SucceedsTwoMinutesAfterTheDeadlineWithNoCloseSweep_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        fixture.Drain();
        FakeClock unswept = TestPolls.At(TestPolls.DefaultDeadline + Duration.FromMinutes(2));

        Assert.Equal(PollStatus.Polling, fixture.Poll.Status);
        PollFinalizationResult result = fixture.Poll.Finalize(fixture.Option(0).Id, unswept);

        Assert.Equal(PollStatus.Finalized, fixture.Poll.Status);
        Assert.NotNull(result.Session);

        IReadOnlyList<IDomainEvent> events = fixture.Drain();
        Assert.Single(events.OfType<PollClosed>());
        Assert.Single(events.OfType<PollOptionSelected>());
    }

    [Fact]
    public void Finalize_SucceedsAtExactlyTheDeadline_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);

        fixture.Poll.Finalize(fixture.Option(0).Id, TestPolls.At(TestPolls.DefaultDeadline));

        Assert.Equal(PollStatus.Finalized, fixture.Poll.Status);
    }

    [Fact]
    public void Finalize_IsRejectedAtExactlyTheSelectionWindowEnd_D088()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        FakeClock tooLate = TestPolls.At(fixture.Poll.SelectionWindowEndsAt);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Finalize(fixture.Option(0).Id, tooLate));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void Finalize_SucceedsOneMinuteBeforeTheSelectionWindowEnds_D088()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        FakeClock justInTime = TestPolls.At(fixture.Poll.SelectionWindowEndsAt - Duration.FromMinutes(1));

        fixture.Poll.Finalize(fixture.Option(0).Id, justInTime);

        Assert.Equal(PollStatus.Finalized, fixture.Poll.Status);
    }

    [Fact]
    public void Finalize_IsRejectedOnAnExpiredPoll_D088()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        FakeClock afterWindow = TestPolls.At(fixture.Poll.SelectionWindowEndsAt + Duration.FromHours(1));
        fixture.Poll.ApplyDueTransitions(afterWindow);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Finalize(fixture.Option(0).Id, afterWindow));

        Assert.Equal(DomainError.WindowClosed, error.Error);
        Assert.Equal(PollStatus.Expired, fixture.Poll.Status);
    }

    [Fact]
    public void Finalize_IsRejectedOnACanceledPoll_D156()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        fixture.Poll.Cancel(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Finalize(fixture.Option(0).Id, SelectionTime));

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void Finalize_IsRejectedASecondTime_D156()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Finalize(chosen, SelectionTime);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Poll.Finalize(chosen, SelectionTime));

        Assert.Equal(DomainError.TerminalState, error.Error);
    }

    [Fact]
    public void Finalize_RejectsAnOptionFromAnotherPoll_D080()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Finalize(PollOptionId.New(), SelectionTime));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    // ---- §6.4-6/7: guests, requests, mutes -----------------------------------------------------

    [Fact]
    public void Finalize_ReParentsGuestsOntoTheSessionWithTheirTokensPreserved_D048()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLink link, string token) = fixture.Poll.CreateGuestLink(clock);
        GuestIdentity identity = fixture.Poll.ClaimGuestLink(link.Id, DisplayName.Create("Robin"), clock);
        fixture.Poll.Vote(identity.Participant, fixture.Option(0).Id, VoteChoice.Available, clock);

        Session session = fixture.Poll.Finalize(fixture.Option(0).Id, SelectionTime).Session;

        Assert.Empty(fixture.Poll.GuestIdentities);
        Assert.Empty(fixture.Poll.GuestLinks);
        Assert.Single(session.GuestIdentities);
        Assert.Equal(session.Id, session.FindLinkByToken(token)!.SessionId);
        Assert.Null(session.GuestLinks[0].PollId);
        Assert.Equal(ParticipationStatus.Going, session.Find(identity.Participant)!.Status);
    }

    [Fact]
    public void Finalize_LapsesPendingJoinRequestsInsteadOfTransferringThem_D118()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        JoinRequest request = fixture.Poll.OpenJoinRequest(UserId.New(), clock);

        Session session = fixture.Poll.Finalize(
            fixture.Option(0).Id, TestPolls.At(TestPolls.DefaultDeadline + Duration.FromMinutes(2))).Session;

        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
        Assert.Empty(session.JoinRequests);
    }

    [Fact]
    public void Finalize_ExposesTheMuteCarryOverPair_D090()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);

        PollFinalizationResult result = fixture.Poll.Finalize(fixture.Option(0).Id, SelectionTime);

        Assert.Equal(fixture.Poll.Id, result.MuteCarryOver.FromPollId);
        Assert.Equal(result.Session.Id, result.MuteCarryOver.ToSessionId);
    }

    [Fact]
    public void Finalize_IgnoresVotesCastOnOtherOptions_D051()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2, minAttendance: 1);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(1).Id, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), fixture.Option(1).Id, VoteChoice.Unavailable, clock);

        Session session = fixture.Poll.Finalize(fixture.Option(0).Id, SelectionTime).Session;

        Assert.Equal(ParticipationStatus.Pending, session.Find(fixture.InviteeRef(0))!.Status);
        Assert.Equal(ParticipationStatus.Pending, session.Find(fixture.InviteeRef(1))!.Status);
    }

    [Fact]
    public void Finalize_UsesTheRevisedChoiceButTheFirstResponseTime_D172()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 2);
        PollOptionId chosen = fixture.Option(0).Id;

        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Maybe, clock);
        clock.Advance(Duration.FromHours(1));
        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Available, clock);
        clock.Advance(Duration.FromHours(1));

        // The revision keeps invitee 0's earlier first-response time, so they still allocate first.
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);

        Session session = fixture.Poll.Finalize(chosen, SelectionTime).Session;

        Assert.Equal(ParticipationStatus.Going, session.Find(fixture.InviteeRef(0))!.Status);
        Assert.False(session.Find(fixture.InviteeRef(0))!.IsWaitlisted);
        Assert.True(session.Find(fixture.InviteeRef(1))!.IsWaitlisted);
    }

    // ---- D-187 early finalization --------------------------------------------------------------

    /// <summary>Two hours in — deep inside the default 24-hour poll's voting window.</summary>
    private static FakeClock EarlyTime => TestPolls.At(TestPolls.Hours(2));

    [Fact]
    public void Finalize_BeforeTheDeadlineOnAViableOption_D187()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 3, minAttendance: 3);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Available, clock);
        fixture.Drain();

        PollFinalizationResult result = fixture.Poll.Finalize(chosen, EarlyTime);

        Assert.Equal(PollStatus.Finalized, fixture.Poll.Status);
        Assert.Equal(chosen, fixture.Poll.SelectedOptionId);
        Assert.Equal(SessionStatus.Confirmed, result.Session.Status);
        Assert.Single(fixture.Drain().OfType<PollOptionSelected>());
    }

    [Fact]
    public void Finalize_BeforeTheDeadlineIsRejectedWhenNoOptionIsViable_D187()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 3, minAttendance: 3);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Finalize(fixture.Option(0).Id, EarlyTime));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
        Assert.Equal(PollStatus.Polling, fixture.Poll.Status);
        Assert.Null(fixture.Poll.SelectedOptionId);
    }

    [Fact]
    public void Finalize_BeforeTheDeadlineIsRejectedOnANonViableOptionWhileAnotherIsViable_D187()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 3, minAttendance: 3);
        PollOptionId viable = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), viable, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), viable, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(2), fixture.Option(1).Id, VoteChoice.Available, clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Finalize(fixture.Option(1).Id, EarlyTime));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
        Assert.Equal(PollStatus.Polling, fixture.Poll.Status);

        fixture.Poll.Finalize(viable, EarlyTime);

        Assert.Equal(PollStatus.Finalized, fixture.Poll.Status);
    }

    [Fact]
    public void Finalize_BeforeTheDeadlineCountsAvailableVotesOnly_D187()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 3, minAttendance: 3);
        PollOptionId chosen = fixture.Option(0).Id;
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Maybe, clock);
        fixture.Poll.Vote(fixture.InviteeRef(2), chosen, VoteChoice.Maybe, clock);

        // Three responders and the organizer, but only two of them map to Going (D-051).
        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Finalize(chosen, EarlyTime));

        Assert.Equal(DomainError.InvalidTransition, error.Error);

        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Available, clock);
        fixture.Poll.Finalize(chosen, EarlyTime);

        Assert.Equal(PollStatus.Finalized, fixture.Poll.Status);
    }

    [Fact]
    public void Finalize_BeforeTheDeadlineProducesTheSameSessionAsAtTheDeadline_D187()
    {
        FakeClock clock = TestPolls.Clock();
        UserId organizer = UserId.New();
        UserId[] invitees = [UserId.New(), UserId.New(), UserId.New(), UserId.New()];

        Poll early = EquivalencePoll(organizer, invitees, clock);
        Poll onTime = EquivalencePoll(organizer, invitees, clock);

        foreach (Poll poll in new[] { early, onTime })
        {
            PollOptionId chosen = poll.Options[0].Id;
            poll.Vote(ParticipantRef.ForUser(invitees[0]), chosen, VoteChoice.Available, clock);
            poll.Vote(ParticipantRef.ForUser(invitees[1]), chosen, VoteChoice.Available, clock);
            poll.Vote(ParticipantRef.ForUser(invitees[2]), chosen, VoteChoice.Available, clock);
            poll.Vote(ParticipantRef.ForUser(invitees[3]), chosen, VoteChoice.Maybe, clock);
        }

        Session earlySession = early.Finalize(early.Options[0].Id, EarlyTime).Session;
        Session onTimeSession = onTime.Finalize(onTime.Options[0].Id, SelectionTime).Session;

        Assert.Equal(onTimeSession.Status, earlySession.Status);
        Assert.Equal(onTimeSession.StartTime, earlySession.StartTime);
        Assert.Equal(
            onTimeSession.Participations.Select(p => (p.Participant, p.Status, p.IsWaitlisted)),
            earlySession.Participations.Select(p => (p.Participant, p.Status, p.IsWaitlisted)));
    }

    private static Poll EquivalencePoll(UserId organizer, IReadOnlyList<UserId> invitees, IClock clock) =>
        Poll.Create(
            organizer,
            TestPolls.Details(minAttendance: 3, maxCapacity: 3),
            PollDeadlineChoice.Hours24,
            [TestPolls.Hours(72), TestPolls.Hours(96)],
            invitees,
            clock);
}
