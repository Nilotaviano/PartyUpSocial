using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Sessions;

namespace PartyUp.Domain.Tests.Notifications;

public class ReminderRulesTests
{
    private static Instant Start => TestSessions.DefaultStart;

    // ---- D-082: offsets ---------------------------------------------------------------------------

    [Fact]
    public void ComputeReminderInstant_UsesOneHourWhenNothingIsChosen_D082()
    {
        Instant? fireAt = ReminderRules.ComputeReminderInstant(
            Start, choice: null, ReminderOffsetExtensions.Default);

        Assert.Equal(ReminderOffset.Hours1, ReminderOffsetExtensions.Default);
        Assert.Equal(Start - Duration.FromHours(1), fireAt);
    }

    [Theory]
    [InlineData(ReminderOffset.Min15, 15)]
    [InlineData(ReminderOffset.Min30, 30)]
    [InlineData(ReminderOffset.Hours1, 60)]
    [InlineData(ReminderOffset.Hours3, 180)]
    [InlineData(ReminderOffset.Hours12, 720)]
    [InlineData(ReminderOffset.Hours24, 1440)]
    public void ComputeReminderInstant_SubtractsEveryOfferedOffset_D082(ReminderOffset offset, int minutes)
    {
        Instant? fireAt = ReminderRules.ComputeReminderInstant(Start, offset, ReminderOffset.Off);

        Assert.Equal(Start - Duration.FromMinutes(minutes), fireAt);
    }

    [Fact]
    public void ComputeReminderInstant_YieldsNothingWhenTheOffsetIsOff_D082()
    {
        Assert.Null(ReminderRules.ComputeReminderInstant(Start, ReminderOffset.Off, ReminderOffset.Hours24));
        Assert.Null(ReminderRules.ComputeReminderInstant(Start, choice: null, ReminderOffset.Off));
    }

    [Fact]
    public void EffectiveOffset_LetsThePerSessionChoiceOverrideTheAccountDefault_D082()
    {
        Assert.Equal(
            ReminderOffset.Min15,
            ReminderRules.EffectiveOffset(ReminderOffset.Min15, ReminderOffset.Hours24));
        Assert.Equal(
            ReminderOffset.Hours24,
            ReminderRules.EffectiveOffset(null, ReminderOffset.Hours24));
    }

    [Fact]
    public void Plan_PrefersThePerSessionReminderChoice_D082()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.SetReminderChoice(fixture.Invitee(0), ReminderOffset.Min15, clock);

        ReminderPlan? plan = ReminderRules.Plan(
            fixture.Session, fixture.Invitee(0), ReminderOffset.Hours24, clock);

        Assert.Equal(Start - Duration.FromMinutes(15), plan!.Value.FireAt);
        Assert.Equal(fixture.Session.Id, plan.Value.SessionId);
    }

    [Fact]
    public void Plan_YieldsNothingForAParticipantWhoTurnedRemindersOff_D082()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.SetReminderChoice(fixture.Invitee(0), ReminderOffset.Off, clock);

        Assert.Null(ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));
    }

    // ---- §9/§12-30: who is reminded ---------------------------------------------------------------

    [Theory]
    [InlineData(ParticipationStatus.Going, true)]
    [InlineData(ParticipationStatus.Maybe, true)]
    public void Plan_RemindsGoingAndMaybeParticipants_D090(ParticipationStatus status, bool expected)
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), status, clock);

        ReminderPlan? plan = ReminderRules.Plan(
            fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock);

        Assert.Equal(expected, plan is not null);
    }

    [Fact]
    public void Plan_RemindsAWaitlistedGoingParticipant_D090()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1, maxCapacity: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        // §12-30: D-090's exception says "Going or Maybe users" with no waitlist qualifier, and D-084
        // makes the selected status real.
        Assert.True(fixture.Row(0).IsWaitlisted);
        Assert.NotNull(ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));
    }

    [Fact]
    public void Plan_YieldsNothingForPendingDeclinedOrRemovedRows_D090()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 3, minAttendance: 1);
        fixture.Session.DeclineInvitation(fixture.InviteeRef(1), clock);
        fixture.Session.RemoveParticipation(fixture.InviteeRef(2), clock);

        Assert.Null(ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));
        Assert.Null(ReminderRules.Plan(fixture.Session, fixture.Invitee(1), ReminderOffset.Hours1, clock));
        Assert.Null(ReminderRules.Plan(fixture.Session, fixture.Invitee(2), ReminderOffset.Hours1, clock));
    }

    [Fact]
    public void Plan_YieldsNothingForANonParticipant_D090()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);

        Assert.Null(ReminderRules.Plan(fixture.Session, UserId.New(), ReminderOffset.Hours1, clock));
    }

    [Fact]
    public void PlanAll_SkipsGuestsEntirely_D167()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        fixture.Session.ClaimGuestLink(link.Id, DisplayName.Create("Ana"), ParticipationStatus.Going, clock);

        IReadOnlyList<ReminderPlan> plans = ReminderRules.PlanAll(
            fixture.Session, _ => ReminderOffset.Hours1, clock);

        Assert.Equal(fixture.Organizer, Assert.Single(plans).UserId);
    }

    [Fact]
    public void PlanAll_CoversEveryGoingOrMaybeParticipantWithTheirOwnDefault_D082()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Maybe, clock);
        fixture.Session.DeclineInvitation(fixture.InviteeRef(1), clock);

        IReadOnlyList<ReminderPlan> plans = ReminderRules.PlanAll(
            fixture.Session,
            user => user == fixture.Organizer ? ReminderOffset.Hours24 : ReminderOffset.Min30,
            clock);

        Assert.Equal(2, plans.Count);
        Assert.Contains(plans, plan => plan.UserId == fixture.Organizer && plan.FireAt == Start - Duration.FromHours(24));
        Assert.Contains(plans, plan => plan.UserId == fixture.Invitee(0) && plan.FireAt == Start - Duration.FromMinutes(30));
    }

    // ---- §9: never fired late ---------------------------------------------------------------------

    [Fact]
    public void Plan_SkipsAReminderWhoseInstantHasAlreadyPassed_D082()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        FakeClock late = TestSessions.At(Start - Duration.FromMinutes(30));

        Assert.Null(ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, late));
        Assert.NotNull(ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Min15, late));
    }

    [Fact]
    public void Plan_StillSchedulesAtExactlyTheReminderInstant_D082()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        FakeClock due = TestSessions.At(Start - Duration.FromHours(1));

        Assert.NotNull(ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, due));
    }

    [Fact]
    public void Plan_SkipsASessionStartingSoonerThanTheChosenOffset_D082()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(
            clock, inviteeCount: 1, minAttendance: 1, startTime: TestSessions.Origin + Duration.FromMinutes(20));
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        // The 1-hour reminder instant is already behind us: never fired late (§9).
        Assert.Null(ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));
    }

    // ---- §9: recompute and cancel triggers --------------------------------------------------------

    [Fact]
    public void ShouldReschedule_IsFalseWhileNothingMoves_D082()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        ReminderPlan plan = ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock)!.Value;

        Assert.False(ReminderRules.ShouldReschedule(
            plan.FireAt, fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));
        Assert.False(ReminderRules.ShouldReschedule(scheduledFor: null, plan: null));
    }

    [Fact]
    public void ShouldReschedule_IsTrueAfterAStartTimeChange_D063()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Instant scheduled = ReminderRules
            .Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock)!.Value.FireAt;

        Instant newStart = Start + Duration.FromHours(3);
        fixture.Session.ChangeStartTime(newStart, clock);

        // The D-063 reset returned the row to Pending: the pending reminder must die.
        Assert.True(ReminderRules.ShouldReschedule(
            scheduled, fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));
        Assert.Null(ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));

        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        Assert.Equal(
            newStart - Duration.FromHours(1),
            ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock)!.Value.FireAt);
    }

    [Fact]
    public void ShouldReschedule_IsFalseAfterADurationChange_D122()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Instant scheduled = ReminderRules
            .Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock)!.Value.FireAt;

        fixture.Session.ChangeDuration(Duration15.FromMinutes(240), clock);

        // A duration change reschedules the session, not the reminder: the start did not move.
        Assert.False(ReminderRules.ShouldReschedule(
            scheduled, fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));
    }

    [Fact]
    public void ShouldReschedule_CancelsWhenTheParticipantLeaves_D055()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Instant scheduled = ReminderRules
            .Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock)!.Value.FireAt;

        fixture.Session.Leave(fixture.InviteeRef(0), clock);

        Assert.True(ReminderRules.ShouldReschedule(
            scheduled, fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));
    }

    [Fact]
    public void ShouldReschedule_CancelsWhenBlockOrDeletionRemovesTheParticipant_D077()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Instant scheduled = ReminderRules
            .Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock)!.Value.FireAt;

        fixture.Session.RemoveParticipation(fixture.InviteeRef(0), clock);

        // §9: a Removed participant's pending reminder must die with their access.
        Assert.True(ReminderRules.ShouldReschedule(
            scheduled, fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));
    }

    [Fact]
    public void ShouldReschedule_CancelsWhenTheSessionIsCanceled_D166()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        Instant scheduled = ReminderRules
            .Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock)!.Value.FireAt;

        fixture.Session.Cancel(clock);

        Assert.Null(ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));
        Assert.True(ReminderRules.ShouldReschedule(
            scheduled, fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock));
    }

    [Fact]
    public void Plan_YieldsNothingForTheUserWhoseDeletionIsExecuting_D092()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        RecipientFilter filter = RecipientFilter.None.Excluding(fixture.Invitee(0));

        Assert.Null(ReminderRules.Plan(
            fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock, filter));
        Assert.NotNull(ReminderRules.Plan(
            fixture.Session, fixture.Organizer, ReminderOffset.Hours1, clock, filter));
    }

    // ---- the record the due plan writes -----------------------------------------------------------

    [Fact]
    public void ToRequest_WritesAStructuredReminderRow_D082()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        ReminderPlan plan = ReminderRules.Plan(fixture.Session, fixture.Invitee(0), ReminderOffset.Hours1, clock)!.Value;

        NotificationRequest request = ReminderRules.ToRequest(plan);
        Notification notification = Notification.From(request, clock);

        Assert.Equal(NotificationType.SessionReminder, request.Type);
        Assert.Equal(fixture.Invitee(0), notification.RecipientUserId);
        Assert.Equal(fixture.Session.Id, notification.SessionId);
    }
}
