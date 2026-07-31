using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Polls;

namespace PartyUp.Domain.Tests.Notifications;

public class PollRecipientTests
{
    /// <summary>Inside the selection window [Deadline, Deadline + 48 h) of the default 24-hour poll.</summary>
    private static FakeClock SelectionTime => TestPolls.At(TestPolls.Hours(25));

    // ---- §10: invitation --------------------------------------------------------------------------

    [Fact]
    public void Invitation_ReachesTheNewInviteeOnly_D112()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);

        PollInvitationSent invitation = TestNotifications.All<PollInvitationSent>(fixture.Drain())[0];
        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.Invitation(invitation, RecipientFilter.None);

        Assert.Equal([fixture.Invitee(0)], TestNotifications.Recipients(requests));
        Assert.Equal(NotificationType.PollInvitation, requests[0].Type);
        Assert.Equal(fixture.Poll.Id, requests[0].Subject.PollId);
        Assert.Null(requests[0].Subject.SessionId);
    }

    // ---- §10: voter-suggested option (D-120) ------------------------------------------------------

    [Fact]
    public void OptionSuggested_ReachesTheOrganizerOnly_D120()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 3);
        _ = fixture.Drain();
        Instant suggested = TestPolls.Hours(120);

        _ = fixture.Poll.SuggestOption(fixture.InviteeRef(0), suggested, clock);

        PollOptionSuggested domainEvent = TestNotifications.Single<PollOptionSuggested>(fixture.Drain());
        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.OptionSuggested(fixture.Poll, domainEvent, RecipientFilter.None);

        Assert.Equal([fixture.Organizer], TestNotifications.Recipients(requests));
        Assert.Equal(suggested, requests[0].Subject.OptionStartTime);
        Assert.Equal(fixture.Invitee(0), requests[0].Subject.ActorUserId);
    }

    [Fact]
    public void OptionSuggested_ReachesTheOrganizerWhenAGuestSuggests_D120()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (_, ParticipantRef guest) = PollGuests.Claim(fixture.Poll, clock, "Ana");
        _ = fixture.Drain();

        _ = fixture.Poll.SuggestOption(guest, TestPolls.Hours(120), clock);

        PollOptionSuggested domainEvent = TestNotifications.Single<PollOptionSuggested>(fixture.Drain());
        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.OptionSuggested(fixture.Poll, domainEvent, RecipientFilter.None);

        Assert.Equal([fixture.Organizer], TestNotifications.Recipients(requests));
        // A guest suggester leaves no actor ref: the row is about the option, not the person (D-167).
        Assert.Null(requests[0].Subject.ActorUserId);
    }

    // ---- §10: poll broadcasts ---------------------------------------------------------------------

    [Fact]
    public void Closed_ReachesEveryActiveRegisteredParticipantAndTheOrganizer_D120()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);
        _ = fixture.Drain();

        fixture.Poll.Close(TestPolls.At(TestPolls.DefaultDeadline));

        PollClosed closed = TestNotifications.Single<PollClosed>(fixture.Drain());
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Closed(fixture.Poll, closed, RecipientFilter.None));

        Assert.Equal(3, recipients.Count);
        Assert.Contains(fixture.Organizer, recipients);
        Assert.Contains(fixture.Invitee(0), recipients);
        Assert.Contains(fixture.Invitee(1), recipients);
    }

    [Fact]
    public void Expired_ReachesEveryRegisteredPollParticipant_D170()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);
        _ = fixture.Drain();

        fixture.Poll.Expire(TestPolls.At(TestPolls.Hours(72)));

        PollExpired expired = TestNotifications.Single<PollExpired>(fixture.Drain());
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Expired(fixture.Poll, expired, RecipientFilter.None));

        Assert.Equal(3, recipients.Count);
        Assert.Contains(fixture.Organizer, recipients);
    }

    [Fact]
    public void Expired_ReachesTheSameAudienceWhenDeletionExpiresThePoll_D170()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);
        _ = fixture.Drain();

        fixture.Poll.ExpireForOrganizerDeletion(clock);

        PollExpired expired = TestNotifications.Single<PollExpired>(fixture.Drain());
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Expired(fixture.Poll, expired, RecipientFilter.None));

        Assert.Contains(fixture.Invitee(0), recipients);
        Assert.Contains(fixture.Invitee(1), recipients);
    }

    [Fact]
    public void Expired_ExcludesTheDeletedOrganizerThroughTheExecutorHook_D170()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);
        _ = fixture.Drain();
        fixture.Poll.ExpireForOrganizerDeletion(clock);

        PollExpired expired = TestNotifications.Single<PollExpired>(fixture.Drain());
        RecipientFilter filter = RecipientFilter.None.Excluding(fixture.Organizer);
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Expired(fixture.Poll, expired, filter));

        // §11 step 5: the deleting user receives nothing from the effects of their own deletion.
        Assert.DoesNotContain(fixture.Organizer, recipients);
        Assert.Equal(2, recipients.Count);
    }

    [Fact]
    public void Expired_ExcludesARemovedVoter_D170()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);
        fixture.Poll.RemoveParticipant(fixture.InviteeRef(0), clock);
        _ = fixture.Drain();

        fixture.Poll.Expire(TestPolls.At(TestPolls.Hours(72)));

        PollExpired expired = TestNotifications.Single<PollExpired>(fixture.Drain());

        Assert.DoesNotContain(
            fixture.Invitee(0),
            TestNotifications.Recipients(
                RecipientComputer.Expired(fixture.Poll, expired, RecipientFilter.None)));
    }

    [Fact]
    public void Canceled_ReachesEveryRegisteredParticipantExceptTheCancelingOrganizer_D166()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);
        _ = fixture.Drain();

        fixture.Poll.Cancel(clock);

        PollCanceled canceled = TestNotifications.Single<PollCanceled>(fixture.Drain());
        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.Canceled(fixture.Poll, canceled, RecipientFilter.None);
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(requests);

        Assert.Equal(2, recipients.Count);
        Assert.DoesNotContain(fixture.Organizer, recipients);
        Assert.Equal(fixture.Organizer, requests[0].Subject.ActorUserId);
    }

    [Fact]
    public void Canceled_ExcludesGuestVoters_D167()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        _ = PollGuests.Claim(fixture.Poll, clock, "Ana");
        _ = fixture.Drain();

        fixture.Poll.Cancel(clock);

        PollCanceled canceled = TestNotifications.Single<PollCanceled>(fixture.Drain());

        Assert.Empty(RecipientComputer.Canceled(fixture.Poll, canceled, RecipientFilter.None));
    }

    // ---- §10: option selected (§6.4-8, §12-16) ----------------------------------------------------

    [Fact]
    public void OptionSelected_ReachesTheWholePollAudienceIncludingTheOrganizer_D120()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2, minAttendance: 1);
        PollOption chosen = fixture.Option(0);
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen.Id, VoteChoice.Available, clock);
        _ = fixture.Drain();

        _ = fixture.Poll.Finalize(chosen.Id, SelectionTime);

        PollOptionSelected selected = TestNotifications.Single<PollOptionSelected>(fixture.Drain());
        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.OptionSelected(fixture.Poll, selected, RecipientFilter.None);

        Assert.Equal(3, requests.Count);
        Assert.Contains(fixture.Organizer, TestNotifications.Recipients(requests));
        Assert.Equal(chosen.StartTime, requests[0].Subject.OptionStartTime);
        Assert.Equal(fixture.Poll.Id, requests[0].Subject.PollId);
        Assert.Equal(fixture.Poll.CreatedSessionId, requests[0].Subject.SessionId);
    }

    [Fact]
    public void OptionSelected_FiresEvenWhenFinalizationMintsAnExpiredSession_D112()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock,
            inviteeCount: 2,
            minAttendance: 3,
            optionStartTimes: [TestPolls.Hours(-48), TestPolls.Hours(96)]);
        PollOption elapsed = fixture.Poll.Options.Single(option => option.StartTime == TestPolls.Hours(-48));
        _ = fixture.Drain();

        Session session = fixture.Poll.Finalize(elapsed.Id, SelectionTime).Session;

        PollOptionSelected selected = TestNotifications.Single<PollOptionSelected>(fixture.Drain());

        // §12-16: the spec §8.4 list has no branch exception — voters must learn the outcome.
        Assert.Equal(SessionStatus.Expired, session.Status);
        Assert.Equal(3, RecipientComputer.OptionSelected(fixture.Poll, selected, RecipientFilter.None).Count);
    }

    [Fact]
    public void WaitlistAdded_ReachesEachParticipantTheAllocationOverflowed_D112()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 2);
        PollOptionId chosen = fixture.Option(0).Id;
        clock.Advance(Duration.FromHours(1));
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen, VoteChoice.Available, clock);
        clock.Advance(Duration.FromHours(1));
        fixture.Poll.Vote(fixture.InviteeRef(1), chosen, VoteChoice.Available, clock);
        _ = fixture.Drain();

        _ = fixture.Poll.Finalize(chosen, SelectionTime);

        PollWaitlistAdded added = TestNotifications.Single<PollWaitlistAdded>(fixture.Drain());
        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.WaitlistAdded(added, RecipientFilter.None);

        Assert.Equal([fixture.Invitee(1)], TestNotifications.Recipients(requests));
        Assert.Equal(NotificationType.WaitlistAdded, requests[0].Type);
        Assert.Equal(fixture.Poll.Id, requests[0].Subject.PollId);
        Assert.Equal(fixture.Poll.CreatedSessionId, requests[0].Subject.SessionId);
    }

    // ---- §10: poll join requests (D-107) ----------------------------------------------------------

    [Fact]
    public void JoinRequestReceived_ReachesTheOrganizer_D107()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        _ = fixture.Drain();

        _ = fixture.Poll.OpenJoinRequest(requester, clock);

        PollJoinRequestReceived received = TestNotifications.Single<PollJoinRequestReceived>(fixture.Drain());
        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.JoinRequestReceived(fixture.Poll, received, RecipientFilter.None);

        Assert.Equal([fixture.Organizer], TestNotifications.Recipients(requests));
        Assert.Equal(requester, requests[0].Subject.ActorUserId);
    }

    [Fact]
    public void JoinRequestAcceptedAndRejected_ReachTheRequesterOnly_D107()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 1);
        UserId accepted = UserId.New();
        UserId rejected = UserId.New();
        JoinRequest first = fixture.Poll.OpenJoinRequest(accepted, clock);
        JoinRequest second = fixture.Poll.OpenJoinRequest(rejected, clock);
        _ = fixture.Drain();

        _ = fixture.Poll.ApproveJoinRequest(first.Id, clock);
        fixture.Poll.RejectJoinRequest(second.Id, clock);

        IReadOnlyList<IDomainEvent> events = fixture.Drain();

        Assert.Equal(
            [accepted],
            TestNotifications.Recipients(RecipientComputer.JoinRequestAccepted(
                TestNotifications.Single<PollJoinRequestAccepted>(events), RecipientFilter.None)));
        Assert.Equal(
            [rejected],
            TestNotifications.Recipients(RecipientComputer.JoinRequestRejected(
                TestNotifications.Single<PollJoinRequestRejected>(events), RecipientFilter.None)));
    }

    // ---- §10: deadline approaching (D-120) --------------------------------------------------------

    [Fact]
    public void DeadlineApproaching_ReachesThePollAudienceOneHourBeforeTheDeadline_D120()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);
        FakeClock noticeTime = TestPolls.At(PollDeadlineNotice.NoticeInstantFor(fixture.Poll));

        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.DeadlineApproaching(fixture.Poll, RecipientFilter.None, noticeTime);

        Assert.Equal(Duration.FromHours(1), PollDeadlineNotice.LeadTime);
        Assert.Equal(TestPolls.Hours(23), noticeTime.GetCurrentInstant());
        Assert.Equal(3, requests.Count);
        Assert.Equal(NotificationType.PollDeadlineApproaching, requests[0].Type);
    }

    [Fact]
    public void DeadlineApproaching_ProducesNothingBeforeItIsDue_D120()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);

        Assert.Empty(RecipientComputer.DeadlineApproaching(
            fixture.Poll, RecipientFilter.None, TestPolls.At(TestPolls.Hours(22))));
    }

    [Fact]
    public void DeadlineApproaching_IsSkippedOnceThePollHasLeftPolling_D120()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);
        fixture.Poll.Cancel(clock);
        FakeClock noticeTime = TestPolls.At(TestPolls.Hours(23));

        Assert.False(PollDeadlineNotice.ShouldSend(fixture.Poll, noticeTime));
        Assert.False(PollDeadlineNotice.ShouldSchedule(fixture.Poll, clock));
        Assert.Empty(RecipientComputer.DeadlineApproaching(fixture.Poll, RecipientFilter.None, noticeTime));
    }

    [Fact]
    public void DeadlineApproaching_IsNeverSentAfterTheDeadline_D120()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);

        Assert.False(PollDeadlineNotice.ShouldSend(fixture.Poll, TestPolls.At(TestPolls.DefaultDeadline)));
        Assert.False(PollDeadlineNotice.ShouldSchedule(fixture.Poll, TestPolls.At(TestPolls.Hours(23) + Duration.FromMinutes(1))));
    }

    [Fact]
    public void DeadlineApproaching_ExcludesGuestVoters_D167()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        _ = PollGuests.Claim(fixture.Poll, clock, "Ana");

        IReadOnlyList<NotificationRequest> requests = RecipientComputer.DeadlineApproaching(
            fixture.Poll, RecipientFilter.None, TestPolls.At(TestPolls.Hours(23)));

        Assert.Equal([fixture.Organizer], TestNotifications.Recipients(requests));
    }

    // ---- dispatcher coverage of the §10 poll rows (D-112) -----------------------------------------

    [Fact]
    public void ForPollEvent_RoutesEveryPollEventToARecipientFunction_D112()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 1);
        PollId id = fixture.Poll.Id;
        UserId subject = fixture.Invitee(0);
        JoinRequestId requestId = JoinRequestId.New();
        SessionId sessionId = SessionId.New();

        IDomainEvent[] events =
        [
            new PollInvitationSent(id, subject),
            new PollOptionSuggested(id, fixture.Option(0).Id, fixture.InviteeRef(0)),
            new PollClosed(id),
            new PollExpired(id),
            new PollCanceled(id, fixture.Organizer),
            new PollOptionSelected(id, fixture.Option(0).Id, sessionId),
            new PollWaitlistAdded(id, sessionId, fixture.InviteeRef(0)),
            new PollJoinRequestReceived(id, requestId, subject),
            new PollJoinRequestAccepted(id, requestId, subject),
            new PollJoinRequestRejected(id, requestId, subject),
        ];

        Assert.All(
            events,
            domainEvent => Assert.NotEmpty(
                RecipientComputer.ForPollEvent(fixture.Poll, domainEvent, RecipientFilter.None)));
    }
}
