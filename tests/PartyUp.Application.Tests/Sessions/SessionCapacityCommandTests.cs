using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SessionCapacityCommandTests
{
    [Fact]
    public async Task GoingAndMaybeConsumeCapacityAndOverflowWaitlists_D062_D084()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: 2, invitees: [fixture.InviteeId, UserId.New()]);
        UserId secondInvitee = session.Participations.First(
            p => p.UserId is { } u && u != fixture.OrganizerId && u != fixture.InviteeId).UserId!.Value;
        SessionParticipationUseCases useCase = fixture.ParticipationUseCases();

        // Organizer already occupies one slot as Going (D-062: only active Going counts toward the
        // minimum, but D-084 says active Going + Maybe both consume the maximum).
        await useCase.RespondToInvitationAsync(session.Id, fixture.InviteeId, ParticipationStatus.Maybe, default);
        Assert.False(session.Find(ParticipantRef.ForUser(fixture.InviteeId))!.IsWaitlisted);
        Assert.Equal(2, session.ActiveOccupancy);

        // A third active consumer overflows the maximum and waitlists.
        await useCase.RespondToInvitationAsync(session.Id, secondInvitee, ParticipationStatus.Going, default);
        Assert.True(session.Find(ParticipantRef.ForUser(secondInvitee))!.IsWaitlisted);
        Assert.Equal(2, session.ActiveOccupancy);
    }
}
