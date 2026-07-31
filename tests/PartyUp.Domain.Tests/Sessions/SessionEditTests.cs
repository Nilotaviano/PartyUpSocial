using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

public class SessionEditTests
{
    [Fact]
    public void ChangeStartTime_ReturnsEveryParticipantToPending_D063()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 3, minAttendance: 1, maxCapacity: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Maybe, clock);
        fixture.Session.DeclineInvitation(fixture.InviteeRef(2), clock);
        Assert.True(fixture.Row(1).IsWaitlisted);

        fixture.Session.ChangeStartTime(TestSessions.Origin + Duration.FromDays(3), clock);

        Assert.All(
            fixture.Session.Participations,
            row =>
            {
                Assert.Equal(ParticipationStatus.Pending, row.Status);
                Assert.False(row.IsWaitlisted);
            });
    }

    [Fact]
    public void ChangeStartTime_ResetsTheOrganizersOwnRow_D063()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);

        fixture.Session.ChangeStartTime(TestSessions.Origin + Duration.FromDays(3), clock);

        Assert.Equal(ParticipationStatus.Pending, fixture.Row(fixture.OrganizerRef).Status);
        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
    }

    [Fact]
    public void ChangeStartTime_RevivesDeclinedRowsButNeverRemovedOnes_D063()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2);
        fixture.Session.DeclineInvitation(fixture.InviteeRef(0), clock);
        fixture.Session.RemoveParticipation(fixture.InviteeRef(1), clock);

        fixture.Session.ChangeStartTime(TestSessions.Origin + Duration.FromDays(4), clock);

        Assert.Equal(ParticipationStatus.Pending, fixture.Row(0).Status);
        Assert.Equal(ParticipationStatus.Removed, fixture.Row(1).Status);
    }

    [Fact]
    public void ChangeStartTime_RaisesTheRescheduleAfterTheReset_D063()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        fixture.Drain();

        fixture.Session.ChangeStartTime(TestSessions.Origin + Duration.FromDays(2), clock);

        IReadOnlyList<IDomainEvent> raised = fixture.Drain();
        Assert.Single(raised.OfType<SessionRescheduled>());
        Assert.Empty(raised.OfType<SessionUnconfirmed>());
    }

    [Fact]
    public void ChangeStartTime_RevalidatesTheD130Window_D130()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        DomainException tooSoon = Assert.Throws<DomainException>(
            () => fixture.Session.ChangeStartTime(TestSessions.Origin + Duration.FromMinutes(5), clock));
        DomainException tooFar = Assert.Throws<DomainException>(
            () => fixture.Session.ChangeStartTime(TestSessions.Origin + Duration.FromDays(400), clock));

        Assert.Equal(DomainError.ValidationFailed, tooSoon.Error);
        Assert.Equal(DomainError.ValidationFailed, tooFar.Error);
        Assert.Equal(TestSessions.DefaultStart, fixture.Session.StartTime);
    }

    [Fact]
    public void ChangeStartTime_IsRejectedOnceTheSessionHasStarted_D168()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);

        clock.Reset(fixture.Session.StartTime);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.ChangeStartTime(clock.GetCurrentInstant() + Duration.FromDays(1), clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void ChangeStartTime_AfterTheResetLeavesTheOrganizerRacingLikeAnyone_D063()
    {
        // §12-5: the organizer's own row resets too, so they can lose the post-reschedule RSVP race.
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 1);
        fixture.Session.ChangeStartTime(TestSessions.Origin + Duration.FromDays(2), clock);

        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.OrganizerRef, ParticipationStatus.Going, clock);

        Assert.False(fixture.Row(0).IsWaitlisted);
        Assert.True(fixture.Row(fixture.OrganizerRef).IsWaitlisted);
    }

    [Fact]
    public void ChangeMinAttendance_IsRejectedOnceTheSessionHasStarted_D168()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);

        clock.Reset(fixture.Session.StartTime);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Session.ChangeMinAttendance(2, clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void ChangeMinAttendance_RejectsAValueAboveTheMaximum_D141()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2, maxCapacity: 4);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Session.ChangeMinAttendance(5, clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
        Assert.Equal(2, fixture.Session.MinAttendance);
    }

    [Fact]
    public void ChangeMinAttendance_RejectsAValueBelowOne_D141()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Session.ChangeMinAttendance(0, clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void ChangeMinAttendance_LoweringConfirmsImmediately_D122()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 3);
        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
        fixture.Drain();

        fixture.Session.ChangeMinAttendance(1, clock);

        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        Assert.Single(fixture.Drain().OfType<SessionConfirmed>());
    }

    [Fact]
    public void ChangeMinAttendance_RaiseKeepsConfirmationWhileAGoingWaitlistedCandidateRemains_D122()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 3, minAttendance: 1, maxCapacity: 3);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Maybe, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(2), ParticipationStatus.Going, clock);
        Assert.True(fixture.Row(2).IsGoingWaitlisted);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        fixture.Drain();

        fixture.Session.ChangeMinAttendance(3, clock);

        Assert.Equal(1, fixture.Session.ActiveGoingCount);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        Assert.Empty(fixture.Drain().OfType<SessionUnconfirmed>());
    }

    [Fact]
    public void ChangeMinAttendance_RaiseUnconfirmsWithoutAGoingWaitlistedCandidate_D122()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 3, minAttendance: 1, maxCapacity: 3);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Maybe, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(2), ParticipationStatus.Maybe, clock);
        Assert.True(fixture.Row(2).IsWaitlisted);
        fixture.Drain();

        fixture.Session.ChangeMinAttendance(3, clock);

        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
        Assert.Single(fixture.Drain().OfType<SessionUnconfirmed>());
    }

    [Fact]
    public void ChangeMaxCapacity_RejectsAReductionBelowActiveOccupancy_D121()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 3);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Maybe, clock);
        Assert.Equal(3, fixture.Session.ActiveOccupancy);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Session.ChangeMaxCapacity(2, clock));

        Assert.Equal(DomainError.CapacityExceeded, error.Error);
        Assert.Equal(3, fixture.Session.MaxCapacity);
    }

    [Fact]
    public void ChangeMaxCapacity_RejectsAValueBelowTheMinimum_D141()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 3, maxCapacity: 5);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Session.ChangeMaxCapacity(2, clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void ChangeMaxCapacity_RaisingNeverAutoPromotes_D121()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Assert.True(fixture.Row(0).IsWaitlisted);
        fixture.Drain();

        fixture.Session.ChangeMaxCapacity(4, clock);

        Assert.True(fixture.Row(0).IsWaitlisted);
        Assert.Single(fixture.Drain().OfType<SessionWaitlistVacancy>());
    }

    [Fact]
    public void ChangeMaxCapacity_ClearingIsRejectedWhileTheWaitlistIsNonEmpty_D091()
    {
        // §12-1: D-091 (no maximum ⇒ no waitlist) and manual promotion cannot both hold otherwise.
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Assert.Equal(1, fixture.Session.WaitlistCount);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Session.ChangeMaxCapacity(null, clock));

        Assert.Equal(DomainError.Conflict, error.Error);
        Assert.Equal(1, fixture.Session.MaxCapacity);
    }

    [Fact]
    public void ChangeMaxCapacity_ClearingIsAllowedOnceTheWaitlistIsDisposedOf_D091()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.RejectFromWaitlist(fixture.InviteeRef(0), clock);

        fixture.Session.ChangeMaxCapacity(null, clock);

        Assert.Null(fixture.Session.MaxCapacity);
        Assert.True(fixture.Session.HasFreeSlot);
    }

    [Fact]
    public void ChangeMaxCapacity_IsSilent_D123()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, maxCapacity: 4);
        fixture.Drain();

        fixture.Session.ChangeMaxCapacity(6, clock);

        Assert.Empty(fixture.Drain().OfType<SessionRescheduled>());
    }

    [Fact]
    public void ChangeDuration_RaisesARescheduleWithoutResettingResponses_D122()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Drain();

        fixture.Session.ChangeDuration(Duration15.FromMinutes(180), clock);

        Assert.Equal(180, fixture.Session.Duration.TotalMinutes);
        Assert.Equal(ParticipationStatus.Going, fixture.Row(0).Status);
        Assert.Single(fixture.Drain().OfType<SessionRescheduled>());
    }

    [Fact]
    public void ChangeDuration_ThatPutsTheEndTimeInThePast_AppliesTheDueTransition_D056()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1, durationMinutes: 240);
        clock.Reset(fixture.Session.StartTime + Duration.FromMinutes(200));
        fixture.Session.ApplyDueTransitions(clock);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);

        fixture.Session.ChangeDuration(Duration15.FromMinutes(60), clock);

        Assert.Equal(SessionStatus.Completed, fixture.Session.Status);
    }

    [Fact]
    public void ChangeTitleGameNotesAndPrivacy_AreSilent_D123()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);
        fixture.Drain();

        fixture.Session.ChangeTitle(SessionTitle.Create("Ranked grind"), clock);
        fixture.Session.ChangeGame(Platform.Steam, SessionGame.FreeText(GameName.Create("Deep Rock Galactic")), clock);
        fixture.Session.ChangeNotes(SessionNotes.Create("Bring beers"), clock);
        fixture.Session.ChangePrivacy(true, clock);

        Assert.Equal("Ranked grind", fixture.Session.Title.Value);
        Assert.Equal(Platform.Steam, fixture.Session.Platform);
        Assert.Equal("Deep Rock Galactic", fixture.Session.Game!.Name.Value);
        Assert.Equal("Bring beers", fixture.Session.Notes.Value);
        Assert.True(fixture.Session.IsPrivate);
        Assert.Empty(fixture.Drain());
    }

    [Fact]
    public void ChangePrivacy_ToNonPrivateIsAllowedForAGuestOnlySession_D123()
    {
        // §12-7: D-099's invited-friend rule is creation-time only.
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);

        fixture.Session.ChangePrivacy(false, clock);

        Assert.False(fixture.Session.IsPrivate);
    }

    [Fact]
    public void Edits_AreRejectedInATerminalState_D056()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);
        fixture.Session.Cancel(clock);

        Assert.Equal(
            DomainError.TerminalState,
            Assert.Throws<DomainException>(() => fixture.Session.ChangeMinAttendance(1, clock)).Error);
        Assert.Equal(
            DomainError.TerminalState,
            Assert.Throws<DomainException>(() => fixture.Session.ChangeMaxCapacity(9, clock)).Error);
        Assert.Equal(
            DomainError.TerminalState,
            Assert.Throws<DomainException>(
                () => fixture.Session.ChangeStartTime(TestSessions.Origin + Duration.FromDays(2), clock)).Error);
        Assert.Equal(
            DomainError.TerminalState,
            Assert.Throws<DomainException>(() => fixture.Session.ChangePrivacy(true, clock)).Error);
    }
}
