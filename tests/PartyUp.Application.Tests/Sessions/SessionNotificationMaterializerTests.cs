using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

/// <summary>
/// Proves the §6.3 notification contract that <c>Infrastructure.Persistence.Repositories
/// .SessionNotificationMaterializer</c> wires up: for every drained Session event, it does nothing
/// but supply the write-time mute lookup and hand off to
/// <see cref="RecipientComputer.ForSessionEvent"/> — the pure §10 recipient function, already
/// available here since <c>PartyUp.Application.Tests</c> transitively references
/// <c>PartyUp.Domain</c>. <c>PartyUp.Application.Tests</c> does not and must not reference
/// <c>PartyUp.Infrastructure</c> (A-dependency-rule), so the EF-backed adapter's mute query and
/// <c>Notification</c> row persistence are exercised by tests/PartyUp.Api.IntegrationTests instead;
/// what is provable here — and is the entire behavioral contract, since the adapter adds no logic of
/// its own — is that this pure function suppresses by mute and never discloses a block cause.
/// </summary>
public sealed class SessionNotificationMaterializerTests
{
    [Fact]
    public async Task BlockDerivedUnconfirmation_HasNoBlockActorOrCause_D105()
    {
        SessionTestFixture fixture = new();
        UserId secondInvitee = UserId.New();
        // Organizer + two invitees Going = 3, meeting a minimum of 3; removing one drops active
        // Going to 2, below the minimum, forcing Unconfirm (D-053/D-074/D-106).
        Session session = fixture.SeedSession(
            minAttendance: 3, maxCapacity: null, invitees: [fixture.InviteeId, secondInvitee]);
        SessionParticipationUseCases participation = fixture.ParticipationUseCases();

        await participation.RespondToInvitationAsync(session.Id, fixture.InviteeId, ParticipationStatus.Going, default);
        await participation.RespondToInvitationAsync(session.Id, secondInvitee, ParticipationStatus.Going, default);
        Assert.Equal(SessionStatus.Confirmed, session.Status);

        // The block/deletion executors are system actors that remove a participation directly
        // (§11 step 3) — never through a block-specific domain method — so the event this raises
        // carries no block direction or actor, only the session id (DOMAIN_MODEL §10).
        session.RemoveParticipation(ParticipantRef.ForUser(secondInvitee), fixture.Clock);
        IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
        SessionUnconfirmed unconfirmed = Assert.Single(events.OfType<SessionUnconfirmed>());

        RecipientFilter filter = RecipientFilter.Create(MuteSet.Empty);
        IReadOnlyList<NotificationRequest> requests = RecipientComputer.ForSessionEvent(session, unconfirmed, filter);

        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal(NotificationType.SessionUnconfirmed, request.Type));
        Assert.All(requests, request => Assert.Null(request.Subject.ActorUserId));
    }

    [Fact]
    public async Task MuteSuppressesEverySessionRecordExceptTheReminder_D090_D113()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionParticipationUseCases participation = fixture.ParticipationUseCases();
        await participation.RespondToInvitationAsync(session.Id, fixture.InviteeId, ParticipationStatus.Going, default);
        _ = session.DequeueEvents();

        NotificationMute organizerMute = NotificationMute.ForSession(fixture.OrganizerId, session.Id, fixture.Clock);
        RecipientFilter filter = RecipientFilter.Create(MuteSet.Create([organizerMute]));

        // Every session-wide broadcast in the §10 matrix is checked: the muted organizer never gets a
        // row for any of them (D-090/D-113). The sole carve-out — the reminder — is not produced by
        // this pipeline at all: ISessionNotificationMaterializer only ever sees drained Session
        // events, never a SessionReminder request, which comes from the wholly separate
        // ISessionReminderHandoffWriter/delivery-job path (§9).
        IDomainEvent[] broadcasts =
        [
            new SessionConfirmed(session.Id),
            new SessionUnconfirmed(session.Id),
            new SessionRescheduled(session.Id),
            new SessionCanceled(session.Id, fixture.OrganizerId),
        ];

        foreach (IDomainEvent domainEvent in broadcasts)
        {
            IReadOnlyList<NotificationRequest> requests = RecipientComputer.ForSessionEvent(session, domainEvent, filter);
            Assert.DoesNotContain(requests, request => request.RecipientUserId == fixture.OrganizerId);
            Assert.DoesNotContain(requests, request => request.Type == NotificationType.SessionReminder);
        }

        // The mute is per-recipient, not a blanket session-wide suppression: an unmuted recipient in
        // the very same audience still gets a row.
        IReadOnlyList<NotificationRequest> confirmedRequests = RecipientComputer.ForSessionEvent(
            session, new SessionConfirmed(session.Id), filter);
        Assert.Contains(confirmedRequests, request => request.RecipientUserId == fixture.InviteeId);
    }

    /// <summary>
    /// F4/DOMAIN_MODEL.md §11 step 5: the excluded user (the subject whose own deletion drained these
    /// events) gets no row even though they would otherwise be an eligible recipient, while an
    /// unexcluded recipient in the same audience is unaffected — the wiring
    /// <see cref="Infrastructure.Persistence.Repositories.SessionNotificationMaterializer"/> threads
    /// into <c>RecipientFilter</c>, proven here at the pure-contract level.
    /// </summary>
    [Fact]
    public async Task ExcludedUserReceivesNoRowEvenIfOtherwiseEligible_D105()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionParticipationUseCases participation = fixture.ParticipationUseCases();
        await participation.RespondToInvitationAsync(session.Id, fixture.InviteeId, ParticipationStatus.Going, default);
        _ = session.DequeueEvents();

        RecipientFilter filter = RecipientFilter.Create(MuteSet.Empty, fixture.OrganizerId);

        IReadOnlyList<NotificationRequest> requests = RecipientComputer.ForSessionEvent(
            session, new SessionConfirmed(session.Id), filter);

        Assert.DoesNotContain(requests, request => request.RecipientUserId == fixture.OrganizerId);
        Assert.Contains(requests, request => request.RecipientUserId == fixture.InviteeId);
    }
}
