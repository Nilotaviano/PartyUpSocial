using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SessionResponseCommandTests
{
    [Fact]
    public async Task InProgressGoingMaybeFlipStaysOpenWhilePendingClosesAtStart_D006_D081_D089()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionParticipationUseCases useCase = fixture.ParticipationUseCases();

        await useCase.RespondToInvitationAsync(
            session.Id, fixture.InviteeId, ParticipationStatus.Going, default);
        Assert.Equal(ParticipationStatus.Going, session.Find(ParticipantRef.ForUser(fixture.InviteeId))!.Status);

        // Move into the in-progress window (after start, before end).
        fixture.Clock.Advance(SessionTestFixture.DefaultStart - SessionTestFixture.Origin + Duration.FromMinutes(5));

        // D-089: an active Going↔Maybe flip stays open while in progress.
        SessionCommandResult flip = await useCase.RespondToInvitationAsync(
            session.Id, fixture.InviteeId, ParticipationStatus.Maybe, default);
        Assert.True(flip.Succeeded);
        Assert.Equal(ParticipationStatus.Maybe, session.Find(ParticipantRef.ForUser(fixture.InviteeId))!.Status);

        // D-081: a still-Pending organizer-invited third party cannot respond after start.
        SessionTestFixture otherFixture = new();
        Session pendingSession = otherFixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionParticipationUseCases pendingUseCase = otherFixture.ParticipationUseCases();
        otherFixture.Clock.Advance(
            SessionTestFixture.DefaultStart - SessionTestFixture.Origin + Duration.FromMinutes(5));

        await Assert.ThrowsAsync<DomainException>(() => pendingUseCase.RespondToInvitationAsync(
            pendingSession.Id, otherFixture.InviteeId, ParticipationStatus.Going, default));
    }

    [Fact]
    public async Task LeaveClosesAtStartAndOnlyOrganizerRejectExitsTheWaitlist_D055_D089()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: 1);
        SessionParticipationUseCases useCase = fixture.ParticipationUseCases();

        await useCase.RespondToInvitationAsync(session.Id, fixture.InviteeId, ParticipationStatus.Going, default);
        Assert.True(session.Find(ParticipantRef.ForUser(fixture.InviteeId))!.IsWaitlisted);

        // Before start, a waitlisted Going participant can still leave (D-055).
        SessionCommandResult left = await useCase.LeaveSessionAsync(session.Id, fixture.InviteeId, default);
        Assert.True(left.Succeeded);
        Assert.Equal(ParticipationStatus.Declined, session.Find(ParticipantRef.ForUser(fixture.InviteeId))!.Status);

        // Leaving again is idempotent.
        SessionCommandResult leftAgain = await useCase.LeaveSessionAsync(session.Id, fixture.InviteeId, default);
        Assert.True(leftAgain.Succeeded);

        // Past start, leaving is closed — only the organizer's waitlist-reject exits a waitlisted row.
        Session started = fixture.SeedSession(minAttendance: 1, maxCapacity: 1, invitees: [UserId.New()]);
        UserId secondInvitee = started.Participations.First(p => p.UserId != fixture.OrganizerId).UserId!.Value;
        await useCase.RespondToInvitationAsync(started.Id, secondInvitee, ParticipationStatus.Going, default);
        fixture.Clock.Advance(SessionTestFixture.DefaultStart - SessionTestFixture.Origin + Duration.FromMinutes(5));

        await Assert.ThrowsAsync<DomainException>(
            () => useCase.LeaveSessionAsync(started.Id, secondInvitee, default));
    }
}
