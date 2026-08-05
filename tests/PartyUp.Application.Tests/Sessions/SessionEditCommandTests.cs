using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SessionEditCommandTests
{
    [Fact]
    public async Task StartChange_RevalidatesTheWindowAndClosesAtStart_D130_D168()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession();
        EditSessionUseCase useCase = fixture.EditUseCase();

        // D-130 still applies to a reschedule, not just to creation.
        await Assert.ThrowsAsync<DomainException>(() => useCase.ExecuteAsync(
            new EditSessionCommand(
                session.Id, fixture.OrganizerId, null,
                SessionTestFixture.Origin + Duration.FromDays(400), null, null, null, null, null, null, null, null),
            default));

        fixture.Clock.Advance(SessionTestFixture.DefaultStart - SessionTestFixture.Origin + Duration.FromMinutes(1));
        await Assert.ThrowsAsync<DomainException>(() => useCase.ExecuteAsync(
            new EditSessionCommand(
                session.Id, fixture.OrganizerId, null,
                fixture.Clock.GetCurrentInstant() + Duration.FromDays(2), null, null, null, null, null, null, null, null),
            default));
    }

    [Fact]
    public async Task StartReset_RevivesDeclinedNotRemovedAndComputesRecipientsAfterTheReset_D063()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession();
        session.DeclineInvitation(ParticipantRef.ForUser(fixture.InviteeId), fixture.Clock);
        _ = session.DequeueEvents();

        (UserId removedOrganizer, UserId removedCounterpart) = fixture.Friendships.MakeAccepted(fixture.Clock);
        Session removalSession = fixture.SeedSession(invitees: [removedCounterpart], organizerId: removedOrganizer);
        removalSession.RemoveParticipation(ParticipantRef.ForUser(removedCounterpart), fixture.Clock);
        _ = removalSession.DequeueEvents();

        EditSessionUseCase useCase = fixture.EditUseCase();
        Instant newStart = SessionTestFixture.DefaultStart + Duration.FromHours(3);

        await useCase.ExecuteAsync(
            new EditSessionCommand(session.Id, fixture.OrganizerId, null, newStart, null, null, null, null, null, null, null, null),
            default);

        Assert.Equal(ParticipationStatus.Pending, session.Find(ParticipantRef.ForUser(fixture.InviteeId))!.Status);

        await useCase.ExecuteAsync(
            new EditSessionCommand(removalSession.Id, removedOrganizer, null, newStart, null, null, null, null, null, null, null, null),
            default);

        Assert.True(removalSession.Find(ParticipantRef.ForUser(removedCounterpart))!.IsRemoved);

        // Reschedule recipients (the materializer call) are computed from the roster shape after the
        // reset, so the revived Declined invitee is present in the drained event's session state.
        (Session materialized, _) = fixture.Materializer.Calls[^2];
        Assert.Equal(ParticipationStatus.Pending, materialized.Find(ParticipantRef.ForUser(fixture.InviteeId))!.Status);
    }

    [Fact]
    public async Task MinChange_ReevaluatesUnderTheGoingWaitlistGrace_D074_D122()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        session.Rsvp(ParticipantRef.ForUser(fixture.InviteeId), ParticipationStatus.Going, fixture.Clock);
        _ = session.DequeueEvents();
        Assert.Equal(SessionStatus.Confirmed, session.Status);

        EditSessionUseCase useCase = fixture.EditUseCase();
        await useCase.ExecuteAsync(
            new EditSessionCommand(session.Id, fixture.OrganizerId, null, null, null, null, null, null, 5, null, null, null),
            default);

        // Active Going (organizer only) now sits below the raised minimum, but no Going-waitlisted
        // candidate exists, and the session already started? No — before start, so it unconfirms.
        Assert.Equal(SessionStatus.Unconfirmed, session.Status);
    }

    [Fact]
    public async Task MaxChange_RejectsBelowOccupancyAndUnlimitedWithWaitlist_D121_D141_D091()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: 1);
        session.Rsvp(ParticipantRef.ForUser(fixture.InviteeId), ParticipationStatus.Going, fixture.Clock);
        _ = session.DequeueEvents();
        Assert.True(session.Find(ParticipantRef.ForUser(fixture.InviteeId))!.IsWaitlisted);

        EditSessionUseCase useCase = fixture.EditUseCase();

        // D-091/§12-1: clearing to unlimited is rejected while the waitlist is non-empty.
        await Assert.ThrowsAsync<DomainException>(() => useCase.ExecuteAsync(
            new EditSessionCommand(
                session.Id, fixture.OrganizerId, null, null, null, null, null, null, null, null, null,
                [SessionEditClearField.MaxCapacity]),
            default));

        // D-121: cannot drop the maximum below current active occupancy.
        await Assert.ThrowsAsync<DomainException>(() => useCase.ExecuteAsync(
            new EditSessionCommand(session.Id, fixture.OrganizerId, null, null, null, null, null, null, null, 0, null, null),
            default));
    }

    [Fact]
    public async Task SilentFieldsEmitNothingAndDurationEmitsReschedule_D122_D123()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession();
        EditSessionUseCase useCase = fixture.EditUseCase();

        await useCase.ExecuteAsync(
            new EditSessionCommand(
                session.Id, fixture.OrganizerId, "New title", null, null, null, null, "New notes",
                null, null, true, null),
            default);

        Assert.Empty(fixture.Materializer.Calls[^1].Events);

        await useCase.ExecuteAsync(
            new EditSessionCommand(session.Id, fixture.OrganizerId, null, null, 180, null, null, null, null, null, null, null),
            default);

        Assert.Contains(fixture.Materializer.Calls[^1].Events, e => e is SessionRescheduled);
        Assert.NotEmpty(fixture.Reminders.Replans);
    }
}
