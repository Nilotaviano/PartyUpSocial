using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SessionGuestRemovalTests
{
    [Fact]
    public async Task RemovePendingOrConfirmedGuest_RevokesAccessReevaluatesCapacityAndMaterializesDerivedEvents_D044()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: 1);
        SessionInvitationUseCases useCase = fixture.InvitationUseCases();

        CreateGuestLinksResult links = await useCase.CreateGuestLinksAsync(session.Id, fixture.OrganizerId, 1, default);
        GuestLinkId linkId = links.GuestLinks![0].GuestLinkId;

        GuestIdentity identity = session.ClaimGuestLink(
            linkId, DisplayName.Create("Guest One"), ParticipationStatus.Going, fixture.Clock);
        _ = session.DequeueEvents();
        Participation guestRow = session.Find(identity.Participant)!;
        Assert.True(guestRow.IsWaitlisted); // D-084: the organizer's one slot is already taken.

        SessionCommandResult removed = await useCase.RemoveSessionGuestAsync(
            session.Id, fixture.OrganizerId, guestRow.Id, default);

        Assert.True(removed.Succeeded);
        Assert.True(session.Find(identity.Participant)!.IsRemoved);
        Assert.Equal(GuestLinkState.RevokedByRemoval, session.GuestLinks.Single(l => l.Id == linkId).State);

        // Guests are never notification recipients (D-167) — only derived Session events materialize.
        (Session materialized, IReadOnlyList<IDomainEvent> events) = fixture.Materializer.Calls[^1];
        Assert.Same(session, materialized);
        Assert.All(events, e => Assert.False(e is SessionInvitationSent));
    }
}
