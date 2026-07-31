using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Sessions;

namespace PartyUp.Domain.Tests.Notifications;

public class SessionRecipientTests
{
    // ---- §10: invitation --------------------------------------------------------------------------

    [Fact]
    public void Invitation_ReachesTheNewInviteeOnly_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2);

        SessionInvitationSent[] invitations = [.. TestNotifications.All<SessionInvitationSent>(fixture.Drain())];
        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.Invitation(invitations[0], RecipientFilter.None);

        Assert.Equal([fixture.Invitee(0)], TestNotifications.Recipients(requests));
        Assert.Equal(NotificationType.SessionInvitation, requests[0].Type);
        Assert.Equal(fixture.Session.Id, requests[0].Subject.SessionId);
    }

    // ---- §10: state-change broadcasts (§12-31) ----------------------------------------------------

    [Fact]
    public void Confirmation_ReachesEveryPendingGoingOrMaybeParticipantAndTheOrganizer_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 3, minAttendance: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Maybe, clock);

        SessionConfirmed confirmed = TestNotifications.Single<SessionConfirmed>(fixture.Drain());
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Confirmed(fixture.Session, confirmed, RecipientFilter.None));

        Assert.Contains(fixture.Organizer, recipients);
        Assert.Contains(fixture.Invitee(0), recipients);
        Assert.Contains(fixture.Invitee(1), recipients);
        // The still-Pending invitee is in scope; the audience is status-scoped, not attendance-scoped.
        Assert.Contains(fixture.Invitee(2), recipients);
        Assert.Equal(4, recipients.Count);
    }

    [Fact]
    public void Confirmation_IncludesTheActorWhoseRsvpCausedIt_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        SessionConfirmed confirmed = TestNotifications.Single<SessionConfirmed>(fixture.Drain());

        // §12-31: state-change broadcasts include the acting user.
        Assert.Contains(
            fixture.Invitee(0),
            TestNotifications.Recipients(
                RecipientComputer.Confirmed(fixture.Session, confirmed, RecipientFilter.None)));
    }

    [Fact]
    public void Confirmation_ExcludesDeclinedAndRemovedRows_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 3, minAttendance: 2, maxCapacity: 4);
        fixture.Session.DeclineInvitation(fixture.InviteeRef(1), clock);
        fixture.Session.Rsvp(fixture.InviteeRef(2), ParticipationStatus.Going, clock);
        fixture.Session.RemoveParticipation(fixture.InviteeRef(2), clock);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        SessionConfirmed confirmed = TestNotifications.All<SessionConfirmed>(fixture.Drain())[^1];
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Confirmed(fixture.Session, confirmed, RecipientFilter.None));

        Assert.DoesNotContain(fixture.Invitee(1), recipients);
        Assert.DoesNotContain(fixture.Invitee(2), recipients);
    }

    [Fact]
    public void Unconfirmation_ReachesTheSameStatusScopedAudience_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Leave(fixture.InviteeRef(0), clock);

        SessionUnconfirmed unconfirmed = TestNotifications.Single<SessionUnconfirmed>(fixture.Drain());
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Unconfirmed(fixture.Session, unconfirmed, RecipientFilter.None));

        Assert.Contains(fixture.Organizer, recipients);
        Assert.Contains(fixture.Invitee(1), recipients);
        // The leaver is now Declined with Origin = Invited: they keep access but are out of the
        // status-scoped broadcast (§12-31).
        Assert.DoesNotContain(fixture.Invitee(0), recipients);
    }

    [Fact]
    public void Reschedule_ReachesTheInviteesTheResetRevived_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 2);
        fixture.Session.DeclineInvitation(fixture.InviteeRef(0), clock);
        _ = fixture.Drain();

        fixture.Session.ChangeStartTime(TestSessions.DefaultStart + Duration.FromHours(2), clock);

        SessionRescheduled rescheduled = TestNotifications.Single<SessionRescheduled>(fixture.Drain());
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Rescheduled(fixture.Session, rescheduled, RecipientFilter.None));

        // D-063 reset ran first, so the revived (now Pending) invitee is told (§5.6).
        Assert.Contains(fixture.Invitee(0), recipients);
        Assert.Contains(fixture.Invitee(1), recipients);
        Assert.Contains(fixture.Organizer, recipients);
    }

    [Fact]
    public void Reschedule_FiresForADurationChangeToo_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1);
        _ = fixture.Drain();

        fixture.Session.ChangeDuration(Duration15.FromMinutes(180), clock);

        SessionRescheduled rescheduled = TestNotifications.Single<SessionRescheduled>(fixture.Drain());

        Assert.NotEmpty(RecipientComputer.Rescheduled(fixture.Session, rescheduled, RecipientFilter.None));
    }

    // ---- §10: cancellation is access-scoped (D-166) -----------------------------------------------

    [Fact]
    public void Cancellation_ReachesEveryRegisteredParticipantWithAccessExceptTheOrganizer_D166()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 3, minAttendance: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.DeclineInvitation(fixture.InviteeRef(1), clock);
        _ = fixture.Drain();

        fixture.Session.Cancel(clock);

        SessionCanceled canceled = TestNotifications.Single<SessionCanceled>(fixture.Drain());
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Canceled(fixture.Session, canceled, RecipientFilter.None));

        Assert.Contains(fixture.Invitee(0), recipients);
        // A Declined invitee keeps rule-1 access (D-019) and is inside the D-166 audience.
        Assert.Contains(fixture.Invitee(1), recipients);
        Assert.Contains(fixture.Invitee(2), recipients);
        // §12-31: the canceler is not a remaining participant of their own cancellation.
        Assert.DoesNotContain(fixture.Organizer, recipients);
        Assert.Equal(3, recipients.Count);
    }

    [Fact]
    public void Cancellation_ExcludesARemovedParticipant_D166()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 2);
        fixture.Session.RemoveParticipation(fixture.InviteeRef(0), clock);
        _ = fixture.Drain();

        fixture.Session.Cancel(clock);

        SessionCanceled canceled = TestNotifications.Single<SessionCanceled>(fixture.Drain());

        Assert.DoesNotContain(
            fixture.Invitee(0),
            TestNotifications.Recipients(
                RecipientComputer.Canceled(fixture.Session, canceled, RecipientFilter.None)));
    }

    [Fact]
    public void Cancellation_ExcludesADepartedNonInvitee_D166()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 2);
        UserId requester = UserId.New();
        JoinRequest request = fixture.Session.OpenJoinRequest(requester, clock);
        fixture.Session.ApproveJoinRequest(request.Id, clock);
        fixture.Session.Leave(ParticipantRef.ForUser(requester), clock);
        _ = fixture.Drain();

        fixture.Session.Cancel(clock);

        SessionCanceled canceled = TestNotifications.Single<SessionCanceled>(fixture.Drain());

        // A voluntarily departed non-invitee holds no rule-1 access left to be cancelled out of.
        Assert.DoesNotContain(
            requester,
            TestNotifications.Recipients(
                RecipientComputer.Canceled(fixture.Session, canceled, RecipientFilter.None)));
    }

    [Fact]
    public void Cancellation_CarriesTheOrganizerAsTheStructuredActor_D166()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1);
        _ = fixture.Drain();
        fixture.Session.Cancel(clock);

        SessionCanceled canceled = TestNotifications.Single<SessionCanceled>(fixture.Drain());
        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.Canceled(fixture.Session, canceled, RecipientFilter.None);

        Assert.Equal(fixture.Organizer, requests[0].Subject.ActorUserId);
    }

    // ---- §10: join requests (D-107) ---------------------------------------------------------------

    [Fact]
    public void JoinRequestReceived_ReachesTheOrganizer_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        _ = fixture.Drain();

        _ = fixture.Session.OpenJoinRequest(requester, clock);

        SessionJoinRequestReceived received = TestNotifications.Single<SessionJoinRequestReceived>(fixture.Drain());
        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.JoinRequestReceived(fixture.Session, received, RecipientFilter.None);

        Assert.Equal([fixture.Organizer], TestNotifications.Recipients(requests));
        Assert.Equal(requester, requests[0].Subject.ActorUserId);
    }

    [Fact]
    public void JoinRequestAccepted_ReachesTheRequesterOnly_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        JoinRequest request = fixture.Session.OpenJoinRequest(requester, clock);
        _ = fixture.Drain();

        fixture.Session.ApproveJoinRequest(request.Id, clock);

        SessionJoinRequestAccepted accepted = TestNotifications.Single<SessionJoinRequestAccepted>(fixture.Drain());

        Assert.Equal(
            [requester],
            TestNotifications.Recipients(RecipientComputer.JoinRequestAccepted(accepted, RecipientFilter.None)));
    }

    [Fact]
    public void JoinRequestRejected_ReachesARequesterWhoHoldsNoParticipation_D107()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1);
        UserId requester = UserId.New();
        JoinRequest request = fixture.Session.OpenJoinRequest(requester, clock);
        _ = fixture.Drain();

        fixture.Session.RejectJoinRequest(request.Id, clock);

        SessionJoinRequestRejected rejected = TestNotifications.Single<SessionJoinRequestRejected>(fixture.Drain());

        Assert.Null(fixture.Session.Find(ParticipantRef.ForUser(requester)));
        Assert.Equal(
            [requester],
            TestNotifications.Recipients(RecipientComputer.JoinRequestRejected(rejected, RecipientFilter.None)));
    }

    // ---- §10: waitlist rows -----------------------------------------------------------------------

    [Fact]
    public void WaitlistAdded_ReachesTheAffectedParticipantOnly_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 1);
        _ = fixture.Drain();

        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        SessionWaitlistAdded added = TestNotifications.Single<SessionWaitlistAdded>(fixture.Drain());

        Assert.Equal(
            [fixture.Invitee(0)],
            TestNotifications.Recipients(RecipientComputer.WaitlistAdded(added, RecipientFilter.None)));
    }

    [Fact]
    public void WaitlistPromoted_ReachesTheAffectedParticipantOnly_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);
        fixture.Session.Leave(fixture.InviteeRef(0), clock);
        _ = fixture.Drain();

        fixture.Session.PromoteFromWaitlist(fixture.InviteeRef(1), clock);

        SessionWaitlistPromoted promoted = TestNotifications.Single<SessionWaitlistPromoted>(fixture.Drain());

        Assert.Equal(
            [fixture.Invitee(1)],
            TestNotifications.Recipients(RecipientComputer.WaitlistPromoted(promoted, RecipientFilter.None)));
    }

    [Fact]
    public void WaitlistRejected_StillReachesTheParticipantTheCommandJustRemoved_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        _ = fixture.Drain();

        fixture.Session.RejectFromWaitlist(fixture.InviteeRef(0), clock);

        SessionWaitlistRejected rejected = TestNotifications.Single<SessionWaitlistRejected>(fixture.Drain());

        // The §10 carve-out: the row is Removed, and the notification is still owed.
        Assert.Equal(ParticipationStatus.Removed, fixture.Row(0).Status);
        Assert.Equal(
            [fixture.Invitee(0)],
            TestNotifications.Recipients(RecipientComputer.WaitlistRejected(rejected, RecipientFilter.None)));
    }

    [Fact]
    public void WaitlistVacancy_ReachesTheOrganizerWhoMustAct_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);
        _ = fixture.Drain();

        fixture.Session.Leave(fixture.InviteeRef(0), clock);

        SessionWaitlistVacancy vacancy = TestNotifications.Single<SessionWaitlistVacancy>(fixture.Drain());

        Assert.Equal(
            [fixture.Organizer],
            TestNotifications.Recipients(
                RecipientComputer.WaitlistVacancy(fixture.Session, vacancy, RecipientFilter.None)));
    }

    // ---- D-167: guests are never recipients -------------------------------------------------------

    [Fact]
    public void WaitlistAdded_ProducesNothingForAGuest_D167()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1, maxCapacity: 1);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        _ = fixture.Drain();

        fixture.Session.ClaimGuestLink(link.Id, DisplayName.Create("Ana"), ParticipationStatus.Going, clock);

        SessionWaitlistAdded added = TestNotifications.Single<SessionWaitlistAdded>(fixture.Drain());

        Assert.True(added.Participant.IsGuest);
        Assert.Empty(RecipientComputer.WaitlistAdded(added, RecipientFilter.None));
    }

    [Fact]
    public void Cancellation_ExcludesGuestsFromTheAudience_D167()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        fixture.Session.ClaimGuestLink(link.Id, DisplayName.Create("Ana"), ParticipationStatus.Going, clock);
        _ = fixture.Drain();

        fixture.Session.Cancel(clock);

        SessionCanceled canceled = TestNotifications.Single<SessionCanceled>(fixture.Drain());

        // The guest's channel is the revoked-link notice, not a notification (D-174/D-167).
        Assert.Empty(RecipientComputer.Canceled(fixture.Session, canceled, RecipientFilter.None));
        Assert.Equal(2, fixture.Session.Participations.Count);
    }

    [Fact]
    public void StateChangeAudience_NeverContainsAGuest_D167()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        fixture.Session.ClaimGuestLink(link.Id, DisplayName.Create("Ana"), ParticipationStatus.Going, clock);

        IReadOnlyList<UserId> audience = RecipientComputer.StateChangeAudience(fixture.Session);

        Assert.Equal([fixture.Organizer], audience);
    }

    // ---- dispatcher coverage of the §10 session rows (D-112) --------------------------------------

    [Fact]
    public void ForSessionEvent_RoutesEverySessionEventToARecipientFunction_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1);
        SessionId id = fixture.Session.Id;
        UserId subject = fixture.Invitee(0);
        ParticipantRef participant = fixture.InviteeRef(0);
        JoinRequestId requestId = JoinRequestId.New();

        IDomainEvent[] events =
        [
            new SessionInvitationSent(id, subject),
            new SessionConfirmed(id),
            new SessionUnconfirmed(id),
            new SessionRescheduled(id),
            new SessionCanceled(id, fixture.Organizer),
            new SessionJoinRequestReceived(id, requestId, subject),
            new SessionJoinRequestAccepted(id, requestId, subject),
            new SessionJoinRequestRejected(id, requestId, subject),
            new SessionWaitlistAdded(id, participant),
            new SessionWaitlistPromoted(id, participant),
            new SessionWaitlistRejected(id, participant),
            new SessionWaitlistVacancy(id),
        ];

        Assert.All(
            events,
            domainEvent => Assert.NotEmpty(
                RecipientComputer.ForSessionEvent(fixture.Session, domainEvent, RecipientFilter.None)));
    }

    [Fact]
    public void ForSessionEvent_IgnoresAnEventWithNoNotificationContract_D112()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1);

        IReadOnlyList<NotificationRequest> requests = RecipientComputer.ForSessionEvent(
            fixture.Session,
            new PartyUp.Domain.Users.UserUsernameChanged(fixture.Organizer),
            RecipientFilter.None);

        Assert.Empty(requests);
    }
}
