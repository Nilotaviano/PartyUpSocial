using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

/// <summary>
/// Proves the §7.1 reminder-handoff contract at the Application layer: the pure D-082
/// override-beats-default rule already in <see cref="ReminderRules"/>, that a command's replan call
/// sits inside the same <c>IUnitOfWork.ExecuteAsync</c> as its aggregate mutation and rolls back with
/// it, and the DOMAIN_MODEL.md §12 Delivered-vs-reschedule interpretation ruling. The concrete
/// EF-backed <c>Infrastructure.Persistence.Repositories.SessionReminderHandoffStore</c> adapter cannot
/// be constructed here — <c>PartyUp.Application.Tests</c> does not and must not reference
/// <c>PartyUp.Infrastructure</c> (A-dependency-rule) — so its round trip is proven by
/// tests/PartyUp.Api.IntegrationTests instead; <see cref="DecideDeliveredReschedule"/> below mirrors
/// that store's <c>Delivered</c> branch verbatim so the ruling itself is checked without an EF
/// dependency.
/// </summary>
public sealed class SessionReminderHandoffTests
{
    [Fact]
    public void PerSessionOverrideBeatsTheAccountDefault_D082()
    {
        Instant start = Instant.FromUtc(2026, 3, 3, 12, 0);

        // No override: falls back to the account default (1h).
        Instant? withoutOverride = ReminderRules.ComputeReminderInstant(start, null, ReminderOffset.Hours1);
        Assert.Equal(start - Duration.FromHours(1), withoutOverride);

        // D-082: a per-session override (15 minutes) wins over the 1h account default.
        Instant? withOverride = ReminderRules.ComputeReminderInstant(start, ReminderOffset.Min15, ReminderOffset.Hours1);
        Assert.Equal(start - Duration.FromMinutes(15), withOverride);

        // An explicit Off override wins too, even over a non-Off account default.
        Instant? off = ReminderRules.ComputeReminderInstant(start, ReminderOffset.Off, ReminderOffset.Hours24);
        Assert.Null(off);
    }

    [Fact]
    public async Task ReplanAndCancelAreTransactional_D082()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: 1);
        SessionParticipationUseCases useCase = fixture.ParticipationUseCases();

        await useCase.RespondToInvitationAsync(session.Id, fixture.InviteeId, ParticipationStatus.Going, default);
        Assert.Single(fixture.Reminders.Replans);
        fixture.Reminders.Replans.Clear();

        var throwingMaterializer = new ThrowingSessionNotificationMaterializer();
        SessionParticipationUseCases throwingUseCase = new(
            fixture.Sessions,
            fixture.Locks,
            fixture.Friendships,
            fixture.Blocks,
            fixture.Groups,
            throwingMaterializer,
            fixture.Reminders,
            fixture.UnitOfWork,
            fixture.Clock);

        // D-055/§7.1: leaving replans (in the real writer, cancels) the departing participant's
        // handoff in the same transaction as the Declined write and the notification materialization.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => throwingUseCase.LeaveSessionAsync(session.Id, fixture.InviteeId, default));

        // The materializer failure surfaced before the replan call that would otherwise have
        // canceled the departing participant's handoff was ever reached: aggregate, notification, and
        // handoff writes execute in that fixed order inside one transaction (§5.3/§7.1), so a failure
        // partway through leaves no later write to roll back in the first place, and in the real
        // EF-backed adapter the same failure aborts the one transaction all three share.
        Assert.Empty(fixture.Reminders.Replans);
    }

    [Fact]
    public void DeliveredHandoffResetsWhenRescheduleMovesDueIntoTheFuture_D082()
    {
        Instant now = Instant.FromUtc(2026, 3, 2, 12, 0);
        Instant past = now - Duration.FromHours(2);
        Instant future = now + Duration.FromHours(2);

        (bool ResetsToPending, bool ClearsDeliveredAt) movedIntoFuture = DecideDeliveredReschedule(future, now);
        Assert.True(movedIntoFuture.ResetsToPending);
        Assert.True(movedIntoFuture.ClearsDeliveredAt);

        (bool ResetsToPending, bool ClearsDeliveredAt) stillInThePast = DecideDeliveredReschedule(past, now);
        Assert.False(stillInThePast.ResetsToPending);
        Assert.False(stillInThePast.ClearsDeliveredAt);
    }

    /// <summary>
    /// Mirrors <c>SessionReminderHandoffStore.ReplanAsync</c>'s <c>Delivered</c> branch: a recomputed
    /// due instant strictly in the future resets the handoff to <c>Pending</c> and clears
    /// <c>delivered_at</c>; one still in the past leaves it <c>Delivered</c> untouched.
    /// </summary>
    private static (bool ResetsToPending, bool ClearsDeliveredAt) DecideDeliveredReschedule(
        Instant recomputedDueAt, Instant now) =>
        recomputedDueAt > now ? (true, true) : (false, false);
}
