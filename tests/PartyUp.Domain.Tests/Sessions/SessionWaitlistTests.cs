using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

public class SessionWaitlistTests
{
    [Fact]
    public void Confirmation_SurvivesADropBelowMinimumWhileAGoingWaitlistedCandidateRemains_D074()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 2, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);
        Assert.True(fixture.Row(1).IsGoingWaitlisted);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        fixture.Drain();

        fixture.Session.Leave(fixture.InviteeRef(0), clock);

        Assert.Equal(1, fixture.Session.ActiveGoingCount);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        Assert.Empty(fixture.Drain().OfType<SessionUnconfirmed>());
    }

    [Fact]
    public void Confirmation_IsNotPreservedByAMaybeWaitlistedCandidate_D074()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 2, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Maybe, clock);
        Assert.True(fixture.Row(1).IsWaitlisted);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        fixture.Drain();

        fixture.Session.Leave(fixture.InviteeRef(0), clock);

        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
        Assert.Single(fixture.Drain().OfType<SessionUnconfirmed>());
    }

    [Fact]
    public void Confirmation_EndsWhenTheLastGoingWaitlistedCandidateIsRejected_D074()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 2, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);
        fixture.Session.Leave(fixture.InviteeRef(0), clock);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        fixture.Drain();

        fixture.Session.RejectFromWaitlist(fixture.InviteeRef(1), clock);

        Assert.Equal(ParticipationStatus.Removed, fixture.Row(1).Status);
        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
        Assert.Single(fixture.Drain().OfType<SessionUnconfirmed>());
    }

    [Fact]
    public void Confirmation_EndsWhenTheGoingWaitlistedCandidateFlipsToMaybe_D074()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 2, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);
        fixture.Session.Leave(fixture.InviteeRef(0), clock);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);

        // The grace candidate turns into a Maybe-waitlisted one: the grace ends with it.
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Maybe, clock);

        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
    }

    [Fact]
    public void RejectFromWaitlist_TargetsTheParticipantItJustRemoved_D084()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Drain();

        fixture.Session.RejectFromWaitlist(fixture.InviteeRef(0), clock);

        SessionWaitlistRejected raised = Assert.Single(fixture.Drain().OfType<SessionWaitlistRejected>());
        Assert.Equal(fixture.InviteeRef(0), raised.Participant);
        Assert.True(fixture.Row(0).IsRemoved);
        Assert.False(fixture.Row(0).IsWaitlisted);
    }

    [Fact]
    public void RejectFromWaitlist_StaysAvailableAfterTheSessionStarts_D089()
    {
        // §12-3: post-start the organizer's reject is the only waitlist exit.
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        clock.Reset(fixture.Session.StartTime + Duration.FromMinutes(10));
        fixture.Session.ApplyDueTransitions(clock);
        fixture.Session.RejectFromWaitlist(fixture.InviteeRef(0), clock);

        Assert.True(fixture.Row(0).IsRemoved);
    }

    [Fact]
    public void RejectFromWaitlist_RejectsAnActiveParticipant_D084()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 4);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.RejectFromWaitlist(fixture.InviteeRef(0), clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void Promote_FillsTheFreedSlotPreservingTheSelectedStatus_D084()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Maybe, clock);
        Assert.True(fixture.Row(1).IsWaitlisted);
        fixture.Session.Leave(fixture.InviteeRef(0), clock);
        fixture.Drain();

        fixture.Session.PromoteFromWaitlist(fixture.InviteeRef(1), clock);

        Assert.False(fixture.Row(1).IsWaitlisted);
        Assert.Equal(ParticipationStatus.Maybe, fixture.Row(1).Status);
        Assert.Single(fixture.Drain().OfType<SessionWaitlistPromoted>());
    }

    [Fact]
    public void Promote_IsCapacityGuarded_D084()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.PromoteFromWaitlist(fixture.InviteeRef(0), clock));

        Assert.Equal(DomainError.CapacityExceeded, error.Error);
    }

    [Fact]
    public void Promote_RejectsANonWaitlistedParticipant_D084()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 4);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.PromoteFromWaitlist(fixture.InviteeRef(0), clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void Promote_OfAGoingWaitlistedCandidate_Confirms_D053()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 2, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);
        Assert.True(fixture.Row(1).IsGoingWaitlisted);
        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
        fixture.Session.Leave(fixture.InviteeRef(0), clock);
        fixture.Drain();

        fixture.Session.PromoteFromWaitlist(fixture.InviteeRef(1), clock);

        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        Assert.Single(fixture.Drain().OfType<SessionConfirmed>());
    }

    [Fact]
    public void AFreedSlot_RaisesTheWaitlistVacancyForTheOrganizer_D084()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);
        fixture.Drain();

        fixture.Session.Leave(fixture.InviteeRef(0), clock);

        SessionWaitlistVacancy raised = Assert.Single(fixture.Drain().OfType<SessionWaitlistVacancy>());
        Assert.Equal(fixture.Session.Id, raised.SessionId);
    }

    [Fact]
    public void AFreedSlot_RaisesNoVacancyWithAnEmptyWaitlist_D084()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 4);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Drain();

        fixture.Session.Leave(fixture.InviteeRef(0), clock);

        Assert.Empty(fixture.Drain().OfType<SessionWaitlistVacancy>());
    }

    [Fact]
    public void AFreedSlot_IsNotReservedForThePromotionChoice_D084()
    {
        // §12-4: a faster RSVP may take the slot the organizer meant to promote into.
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 3, minAttendance: 1, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);
        Assert.True(fixture.Row(1).IsWaitlisted);

        fixture.Session.Leave(fixture.InviteeRef(0), clock);
        fixture.Session.Rsvp(fixture.InviteeRef(2), ParticipationStatus.Going, clock);

        Assert.False(fixture.Row(2).IsWaitlisted);
        Assert.True(fixture.Row(1).IsWaitlisted);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.PromoteFromWaitlist(fixture.InviteeRef(1), clock));
        Assert.Equal(DomainError.CapacityExceeded, error.Error);
    }

    [Fact]
    public void AWaitlistedParticipant_MayLeaveAndReRsvpIntoAFreedSlot_D084()
    {
        // §12-4: the roundabout route past manual promotion, valid before the start.
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);
        Assert.True(fixture.Row(1).IsWaitlisted);

        fixture.Session.Leave(fixture.InviteeRef(0), clock);
        fixture.Session.Leave(fixture.InviteeRef(1), clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);

        Assert.False(fixture.Row(1).IsWaitlisted);
        Assert.Equal(0, fixture.Session.WaitlistCount);
    }

    [Fact]
    public void RemoveParticipation_FreesCapacityAndReevaluatesConfirmation_D077()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);

        fixture.Session.RemoveParticipation(fixture.InviteeRef(0), clock);

        Assert.True(fixture.Row(0).IsRemoved);
        Assert.Equal(1, fixture.Session.ActiveOccupancy);
        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
    }

    [Fact]
    public void RemoveParticipation_IsIdempotent_D077()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        fixture.Session.RemoveParticipation(fixture.InviteeRef(0), clock);

        fixture.Session.RemoveParticipation(fixture.InviteeRef(0), clock);

        Assert.True(fixture.Row(0).IsRemoved);
    }
}
