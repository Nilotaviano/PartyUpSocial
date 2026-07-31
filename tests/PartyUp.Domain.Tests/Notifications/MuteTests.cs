using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Polls;
using PartyUp.Domain.Tests.Sessions;

namespace PartyUp.Domain.Tests.Notifications;

/// <summary>
/// §9: mute is applied at <em>write</em> time — a muted recipient gets no record at all, not merely
/// no push (D-090/D-113) — and the reminder is its only exception.
/// </summary>
public class MuteTests
{
    [Fact]
    public void Confirmation_ProducesNoRecordForAMutedParticipant_D090()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        SessionConfirmed confirmed = TestNotifications.Single<SessionConfirmed>(fixture.Drain());

        RecipientFilter filter = TestNotifications.MutedOn(clock, fixture.Invitee(1), fixture.Session.Id);
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Confirmed(fixture.Session, confirmed, filter));

        Assert.DoesNotContain(fixture.Invitee(1), recipients);
        Assert.Contains(fixture.Invitee(0), recipients);
        Assert.Contains(fixture.Organizer, recipients);
    }

    [Fact]
    public void Cancellation_ProducesNoRecordForAMutedParticipant_D113()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2);
        _ = fixture.Drain();
        fixture.Session.Cancel(clock);
        SessionCanceled canceled = TestNotifications.Single<SessionCanceled>(fixture.Drain());

        RecipientFilter filter = TestNotifications.MutedOn(clock, fixture.Invitee(0), fixture.Session.Id);
        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Canceled(fixture.Session, canceled, filter));

        // D-113 names cancellation explicitly: muting suppresses it like every other session push.
        Assert.Equal([fixture.Invitee(1)], recipients);
    }

    [Fact]
    public void ExplicitTargets_AreMutedToo_D113()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        _ = fixture.Drain();
        fixture.Session.RejectFromWaitlist(fixture.InviteeRef(0), clock);
        SessionWaitlistRejected rejected = TestNotifications.Single<SessionWaitlistRejected>(fixture.Drain());

        RecipientFilter filter = TestNotifications.MutedOn(clock, fixture.Invitee(0), fixture.Session.Id);

        // The §10 carve-out overrides visibility, not muting: "every other session push" is literal.
        Assert.Empty(RecipientComputer.WaitlistRejected(rejected, filter));
    }

    [Fact]
    public void SessionMute_LeavesOtherSessionsAlone_D090()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest muted = TestSessions.New(clock, inviteeCount: 1);
        SessionUnderTest other = TestSessions.New(clock, inviteeCount: 1);
        _ = muted.Drain();
        _ = other.Drain();
        other.Session.Cancel(clock);
        SessionCanceled canceled = TestNotifications.Single<SessionCanceled>(other.Drain());

        RecipientFilter filter = TestNotifications.MutedOn(clock, other.Invitee(0), muted.Session.Id);

        Assert.Equal(
            [other.Invitee(0)],
            TestNotifications.Recipients(RecipientComputer.Canceled(other.Session, canceled, filter)));
    }

    [Fact]
    public void PollBroadcasts_ProduceNoRecordForAMutedVoter_D113()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);
        _ = fixture.Drain();
        fixture.Poll.Close(TestPolls.At(TestPolls.DefaultDeadline));
        PollClosed closed = TestNotifications.Single<PollClosed>(fixture.Drain());

        RecipientFilter filter = TestNotifications.MutedOn(clock, fixture.Invitee(0), fixture.Poll.Id);

        Assert.DoesNotContain(
            fixture.Invitee(0),
            TestNotifications.Recipients(RecipientComputer.Closed(fixture.Poll, closed, filter)));
    }

    [Fact]
    public void FinalizationRows_HonourThePollMuteThatCarriedOntoTheSession_D113()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2, minAttendance: 1);
        PollOption chosen = fixture.Option(0);
        fixture.Poll.Vote(fixture.InviteeRef(0), chosen.Id, VoteChoice.Available, clock);
        _ = fixture.Drain();
        _ = fixture.Poll.Finalize(chosen.Id, TestPolls.At(TestPolls.Hours(25)));
        PollOptionSelected selected = TestNotifications.Single<PollOptionSelected>(fixture.Drain());

        RecipientFilter filter = TestNotifications.MutedOn(clock, fixture.Invitee(0), fixture.Poll.Id);

        // §12-15: the user muted the plan, not the artifact — and unmuting never backfills.
        Assert.DoesNotContain(
            fixture.Invitee(0),
            TestNotifications.Recipients(
                RecipientComputer.OptionSelected(fixture.Poll, selected, filter)));
    }

    [Fact]
    public void FriendRequests_AreNeverMutedBySessionOrPollMutes_D105()
    {
        FakeClock clock = TestSessions.Clock();
        UserId requester = UserId.New();
        UserId addressee = UserId.New();
        RecipientFilter filter = TestNotifications.MutedOn(clock, addressee, SessionId.New());

        IReadOnlyList<NotificationRequest> requests = RecipientComputer.FriendRequest(
            new FriendRequestReceived(FriendshipId.New(), requester, addressee),
            filter);

        Assert.Equal([addressee], TestNotifications.Recipients(requests));
    }

    [Fact]
    public void Reminder_SurvivesTheMuteThatSuppressesEveryOtherRow_D090()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        RecipientFilter filter = TestNotifications.MutedOn(clock, fixture.Invitee(0), fixture.Session.Id);

        ReminderPlan? plan = ReminderRules.Plan(
            fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock, filter);

        // D-090's sole exception: the separately configured reminder for Going or Maybe users.
        Assert.NotNull(plan);
        Assert.Equal(TestSessions.DefaultStart - Duration.FromHours(1), plan.Value.FireAt);
    }
}
