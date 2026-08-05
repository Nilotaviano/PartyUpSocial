using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SessionGuestLinkCommandTests
{
    [Fact]
    public async Task CreateLinksAreBoundedUnclaimedAndReserveNoCapacity_D115()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: 1);
        SessionInvitationUseCases useCase = fixture.InvitationUseCases();
        int occupancyBefore = session.ActiveOccupancy;

        CreateGuestLinksResult result = await useCase.CreateGuestLinksAsync(
            session.Id, fixture.OrganizerId, 3, default);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.GuestLinks!.Count);
        Assert.Equal(3, session.GuestLinks.Count);
        Assert.All(session.GuestLinks, link => Assert.False(link.IsClaimed));
        Assert.Equal(occupancyBefore, session.ActiveOccupancy);

        CreateGuestLinksResult tooMany = await useCase.CreateGuestLinksAsync(
            session.Id, fixture.OrganizerId, SessionInvitationUseCases.MaxGuestLinkCount + 1, default);
        Assert.False(tooMany.Succeeded);
        Assert.Equal("session_validation", tooMany.Code);
    }

    [Fact]
    public async Task RevokeUnclaimedLinkIsIdempotentAndHasNoRosterEffect_D150()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: 1);
        SessionInvitationUseCases useCase = fixture.InvitationUseCases();
        int occupancyBefore = session.ActiveOccupancy;

        CreateGuestLinksResult created = await useCase.CreateGuestLinksAsync(session.Id, fixture.OrganizerId, 1, default);
        GuestLinkId linkId = created.GuestLinks![0].GuestLinkId;

        SessionCommandResult revoked = await useCase.RevokeGuestLinkAsync(session.Id, fixture.OrganizerId, linkId, default);
        Assert.True(revoked.Succeeded);
        Assert.False(session.GuestLinks.Single(l => l.Id == linkId).IsActive);
        Assert.Equal(occupancyBefore, session.ActiveOccupancy);

        // Idempotent: revoking an already-revoked link is a no-op success, not an error.
        SessionCommandResult revokedAgain = await useCase.RevokeGuestLinkAsync(
            session.Id, fixture.OrganizerId, linkId, default);
        Assert.True(revokedAgain.Succeeded);
    }
}
