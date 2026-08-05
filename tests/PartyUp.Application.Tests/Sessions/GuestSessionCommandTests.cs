using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class GuestSessionCommandTests
{
    [Fact]
    public async Task ClaimRenameAndRespondUseTheParentLockWithLastWriteWins_D017_D126()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        (GuestLink link, string _) = session.CreateGuestLink(fixture.Clock);
        _ = session.DequeueEvents();

        GuestSessionUseCases useCase = new(
            fixture.Sessions, fixture.Reads, fixture.Locks, fixture.Materializer, fixture.UnitOfWork, fixture.Clock);

        // D-017: the first visit supplies a display name and a response.
        GuestCommandResult claimed = await useCase.ClaimGuestAsync(
            session.Id, link.Id, DisplayName.Create("Casey"), ParticipationStatus.Going, default);
        Assert.True(claimed.Succeeded);
        GuestIdentityId identityId = claimed.GuestIdentityId!.Value;
        Assert.Equal(ParticipationStatus.Going, session.Find(ParticipantRef.ForGuest(identityId))!.Status);

        GuestCommandResult renamed = await useCase.RenameGuestAsync(
            session.Id, link.Id, DisplayName.Create("Casey R"), default);
        Assert.True(renamed.Succeeded);
        Assert.Equal("Casey R", session.GuestIdentities.Single(g => g.Id == identityId).DisplayName.Value);

        // D-126: a second device's response overwrites the first with no version/conflict check —
        // last write wins, never a rejected conflict.
        GuestCommandResult toMaybe = await useCase.RespondAsGuestAsync(
            session.Id, link.Id, ParticipationStatus.Maybe, default);
        Assert.True(toMaybe.Succeeded);
        Assert.Equal(ParticipationStatus.Maybe, session.Find(ParticipantRef.ForGuest(identityId))!.Status);

        GuestCommandResult backToGoing = await useCase.RespondAsGuestAsync(
            session.Id, link.Id, ParticipationStatus.Going, default);
        Assert.True(backToGoing.Succeeded);
        Assert.Equal(ParticipationStatus.Going, session.Find(ParticipantRef.ForGuest(identityId))!.Status);

        // Every guest mutation locked the session parent — never absence of the lock — and, because
        // a guest response introduces no new registered counterpart, never a users row either.
        Assert.Equal(4, fixture.Locks.LockedSessionBatches.Count);
        Assert.All(fixture.Locks.LockedSessionBatches, batch => Assert.Contains(session.Id, batch));
        Assert.Empty(fixture.Locks.LockedUserBatches);
    }

    [Fact]
    public async Task GuestsAreNeverNotificationRecipients_D167()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: 1, invitees: [fixture.InviteeId]);
        SessionParticipationUseCases participation = fixture.ParticipationUseCases();
        await participation.RespondToInvitationAsync(session.Id, fixture.InviteeId, ParticipationStatus.Going, default);

        (GuestLink link, string _) = session.CreateGuestLink(fixture.Clock);
        _ = session.DequeueEvents();

        GuestSessionUseCases useCase = new(
            fixture.Sessions, fixture.Reads, fixture.Locks, fixture.Materializer, fixture.UnitOfWork, fixture.Clock);

        // The single slot is already taken by the registered invitee, so the guest's claim waitlists
        // (D-084) and raises the §10 "affected participant" event.
        GuestCommandResult claimed = await useCase.ClaimGuestAsync(
            session.Id, link.Id, DisplayName.Create("Riley"), ParticipationStatus.Going, default);
        Assert.True(claimed.Succeeded);

        (Session recordedSession, IReadOnlyList<IDomainEvent> events) = fixture.Materializer.Calls.Last();
        SessionWaitlistAdded waitlistEvent = Assert.Single(events.OfType<SessionWaitlistAdded>());
        Assert.True(waitlistEvent.Participant.IsGuest);

        // D-167: a guest ParticipantRef carries no UserId, so RecipientComputer's explicit-target
        // resolution — the same pure function every real notification write goes through — produces
        // nothing at all for it, on no mute and no exclusion.
        IReadOnlyList<NotificationRequest> requests = RecipientComputer.ForSessionEvent(
            recordedSession, waitlistEvent, RecipientFilter.None);
        Assert.Empty(requests);
    }
}
