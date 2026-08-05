using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SessionCancelCommandTests
{
    [Fact]
    public async Task CompletedCancelsExpiredDoesNotAndRepeatIsIdempotent_D156_D157()
    {
        SessionTestFixture fixture = new();
        Session completedSession = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        EditSessionUseCase useCase = fixture.EditUseCase();

        fixture.Clock.Advance(SessionTestFixture.DefaultStart - SessionTestFixture.Origin + Duration.FromHours(3));
        EditSessionResult canceled = await useCase.CancelAsync(completedSession.Id, fixture.OrganizerId, default);
        Assert.True(canceled.Succeeded);
        Assert.Equal(SessionStatus.Canceled, completedSession.Status);

        // Repeating cancel on an already-Canceled session is idempotent.
        EditSessionResult canceledAgain = await useCase.CancelAsync(completedSession.Id, fixture.OrganizerId, default);
        Assert.True(canceledAgain.Succeeded);
        Assert.Equal(SessionStatus.Canceled, completedSession.Status);

        SessionTestFixture unconfirmedFixture = new();
        Session expiredSession = unconfirmedFixture.SeedSession(minAttendance: 5, maxCapacity: null);
        EditSessionUseCase expiredUseCase = unconfirmedFixture.EditUseCase();
        unconfirmedFixture.Clock.Advance(
            SessionTestFixture.DefaultStart - SessionTestFixture.Origin + Duration.FromHours(3));

        await Assert.ThrowsAsync<DomainException>(
            () => expiredUseCase.CancelAsync(expiredSession.Id, unconfirmedFixture.OrganizerId, default));
    }

    [Fact]
    public async Task CancellationIsAccessScopedAndRevokesEveryGuestLink_D166_D174()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        EditSessionUseCase editUseCase = fixture.EditUseCase();
        SessionInvitationUseCases invitationUseCase = fixture.InvitationUseCases();

        CreateGuestLinksResult links = await invitationUseCase.CreateGuestLinksAsync(
            session.Id, fixture.OrganizerId, 2, default);
        Assert.True(links.Succeeded);

        EditSessionResult canceled = await editUseCase.CancelAsync(session.Id, fixture.OrganizerId, default);

        Assert.True(canceled.Succeeded);
        Assert.All(session.GuestLinks, link => Assert.Equal(GuestLinkState.RevokedByCancel, link.State));

        // D-166: access-scoped notification recipients exclude the organizer (the canceler).
        IReadOnlyList<UserId> audience = PartyUp.Domain.Notifications.RecipientComputer.CancellationAudience(session);
        Assert.DoesNotContain(fixture.OrganizerId, audience);
        Assert.Contains(fixture.InviteeId, audience);
    }
}
