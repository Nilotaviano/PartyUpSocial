using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SessionWaitlistCommandTests
{
    [Fact]
    public async Task PromotionFillsAFreeSlotWithoutReservingItAndNotifiesTheSubject_D084_D112()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: 1);
        SessionParticipationUseCases responses = fixture.ParticipationUseCases();
        SessionJoinRequestUseCases waitlist = fixture.JoinRequestUseCases();

        await responses.RespondToInvitationAsync(session.Id, fixture.InviteeId, ParticipationStatus.Going, default);
        Participation waitlisted = session.Find(ParticipantRef.ForUser(fixture.InviteeId))!;
        Assert.True(waitlisted.IsWaitlisted);

        // Free the one slot the organizer holds, so promotion has somewhere to place the waitlisted row.
        await responses.LeaveSessionAsync(session.Id, fixture.OrganizerId, default);

        SessionCommandResult promoted = await waitlist.PromoteWaitlistParticipantAsync(
            session.Id, fixture.OrganizerId, waitlisted.Id, default);

        Assert.True(promoted.Succeeded);
        Assert.False(session.Find(ParticipantRef.ForUser(fixture.InviteeId))!.IsWaitlisted);
        Assert.Contains((session.Id, fixture.InviteeId), fixture.Reminders.Replans);
    }

    [Fact]
    public async Task OrganizerRejectRemovesTheWaitlistedEntryAndNotifiesTheSubject_D084_D112()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: 1);
        SessionParticipationUseCases responses = fixture.ParticipationUseCases();
        SessionJoinRequestUseCases waitlist = fixture.JoinRequestUseCases();

        await responses.RespondToInvitationAsync(session.Id, fixture.InviteeId, ParticipationStatus.Going, default);
        Participation waitlisted = session.Find(ParticipantRef.ForUser(fixture.InviteeId))!;

        SessionCommandResult rejected = await waitlist.RejectWaitlistParticipantAsync(
            session.Id, fixture.OrganizerId, waitlisted.Id, default);

        Assert.True(rejected.Succeeded);
        Assert.True(session.Find(ParticipantRef.ForUser(fixture.InviteeId))!.IsRemoved);
        Assert.Contains((session.Id, fixture.InviteeId), fixture.Reminders.Replans);

        // Removal is permanent — a second reject attempt fails.
        await Assert.ThrowsAsync<DomainException>(() => waitlist.RejectWaitlistParticipantAsync(
            session.Id, fixture.OrganizerId, waitlisted.Id, default));
    }
}
