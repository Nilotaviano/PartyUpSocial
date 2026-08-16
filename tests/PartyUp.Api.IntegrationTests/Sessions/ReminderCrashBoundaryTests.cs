using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Jobs;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// P4-REMIND-01's crash-boundary gate (PHASE4_DESIGN.md §7.2, A-066): the reminder delivery
/// transaction is all-or-nothing. A failure partway through — after the handoff's attempts were
/// bumped and the notification insert had already run — must roll back every bit of that work, not
/// just skip the final state flip, so the handoff is exactly as if the attempt never happened and the
/// row is still Pending and due for the next real discovery. A different, independently seeded and
/// genuinely committed handoff must be unaffected by that rollback.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReminderCrashBoundaryTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RollbackLeavesNoDueRowAndCommittedRowSurvivesDiscovery_D082()
    {
        (Session rollbackSession, UserId rollbackRecipient, Guid rollbackHandoffId) =
            await SeedEligibleDueHandoffAsync("crash-rollback");
        (Session commitSession, UserId commitRecipient, Guid commitHandoffId) =
            await SeedEligibleDueHandoffAsync("crash-commit");

        Instant now = _factory.Clock.GetCurrentInstant();

        // Simulate a crash partway through the rollback session's per-session transaction: do exactly
        // what SessionReminderDueJob does for a deliverable candidate — bump attempts, insert the
        // notification via the same raw-SQL ON CONFLICT path, flip the handoff to Delivered — and then
        // fail before commit, exercising the same IUnitOfWork.ExecuteAsync boundary the job relies on.
        await using (AsyncServiceScope crashScope = _factory.Services.CreateAsyncScope())
        {
            var unitOfWork = crashScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var sessionLocks = crashScope.ServiceProvider.GetRequiredService<ISessionLockRepository>();
            var candidateStore = crashScope.ServiceProvider.GetRequiredService<SessionDueCandidateStore>();

            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteAsync<bool>(
                async ct =>
                {
                    await sessionLocks.LockSessionsAsync([rollbackSession.Id], ct);
                    await candidateStore.LockSessionHandoffsAsync(rollbackSession.Id, ct);

                    SessionReminderHandoff handoff = await candidateStore.FindHandoffAsync(rollbackHandoffId, ct)
                        ?? throw new InvalidOperationException("Seed handoff missing.");

                    handoff.Attempts++;
                    handoff.LastAttemptAt = now;
                    await candidateStore.InsertReminderNotificationIfAbsentAsync(
                        handoff.Id, rollbackRecipient, rollbackSession.Id, now, ct);
                    handoff.State = SessionReminderHandoffState.Delivered;
                    handoff.DeliveredAt = now;

                    throw new InvalidOperationException("Simulated crash before commit.");
                },
                default));
        }

        // Nothing from the crashed attempt survived: no notification row, and the handoff is exactly
        // as it was discovered.
        await using (AsyncServiceScope postCrashVerify = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = postCrashVerify.ServiceProvider.GetRequiredService<AppDbContext>();
            SessionReminderHandoff afterCrash = await context.SessionReminderHandoffs.AsNoTracking()
                .SingleAsync(h => h.Id == rollbackHandoffId);
            Assert.Equal(SessionReminderHandoffState.Pending, afterCrash.State);
            Assert.Equal(0, afterCrash.Attempts);
            Assert.Null(afterCrash.DeliveredAt);

            bool notificationSurvived = await context.Notifications.AnyAsync(n =>
                n.SessionId == rollbackSession.Id &&
                n.RecipientUserId == rollbackRecipient &&
                n.Type == NotificationType.SessionReminder);
            Assert.False(notificationSurvived, "The crashed attempt's insert must not have committed.");
        }

        // The real job then runs both due handoffs: the rolled-back one is still Pending/due, so a
        // genuine, uncrashed run discovers and delivers it exactly once; the independently committed
        // row from the other session delivers too, proving the rollback above touched only its own
        // session's transaction.
        await using (AsyncServiceScope jobScope = _factory.Services.CreateAsyncScope())
        {
            var job = jobScope.ServiceProvider.GetRequiredService<SessionReminderDueJob>();
            await job.ExecuteAsync(default);
        }

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        SessionReminderHandoff rolledBackThenDelivered = await verifyContext.SessionReminderHandoffs
            .AsNoTracking()
            .SingleAsync(h => h.Id == rollbackHandoffId);
        Assert.Equal(SessionReminderHandoffState.Delivered, rolledBackThenDelivered.State);
        Assert.Equal(1, rolledBackThenDelivered.Attempts);

        int rollbackSessionReminders = await verifyContext.Notifications.CountAsync(n =>
            n.SessionId == rollbackSession.Id &&
            n.RecipientUserId == rollbackRecipient &&
            n.Type == NotificationType.SessionReminder);
        Assert.Equal(1, rollbackSessionReminders);

        SessionReminderHandoff committed = await verifyContext.SessionReminderHandoffs.AsNoTracking()
            .SingleAsync(h => h.Id == commitHandoffId);
        Assert.Equal(SessionReminderHandoffState.Delivered, committed.State);

        int commitSessionReminders = await verifyContext.Notifications.CountAsync(n =>
            n.SessionId == commitSession.Id &&
            n.RecipientUserId == commitRecipient &&
            n.Type == NotificationType.SessionReminder);
        Assert.Equal(1, commitSessionReminders);
    }

    private async Task<(Session Session, UserId Recipient, Guid HandoffId)> SeedEligibleDueHandoffAsync(string label)
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"{label}-organizer@example.test");
        UserId recipientId = await _factory.SeedUserAsync(email: $"{label}-recipient@example.test");

        SessionDetails details = new(
            SessionTitle.Create("Reminder crash-boundary test session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(90),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: (true ? PartyUp.Domain.Kernel.ContentVisibility.Private : PartyUp.Domain.Kernel.ContentVisibility.Public));
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        _ = session.DequeueEvents();
        session.Invite(recipientId, _factory.Clock);
        session.Rsvp(ParticipantRef.ForUser(recipientId), ParticipationStatus.Going, _factory.Clock);
        _ = session.DequeueEvents();

        Guid handoffId = Guid.NewGuid();
        Instant due = _factory.Clock.GetCurrentInstant() - Duration.FromMinutes(1);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Sessions.Add(session);
        context.SessionReminderHandoffs.Add(new SessionReminderHandoff
        {
            Id = handoffId,
            SessionId = session.Id,
            RecipientUserId = recipientId,
            DueAt = due,
            State = SessionReminderHandoffState.Pending,
            PlannedAt = due,
            DeliveredAt = null,
            Version = 0,
            Attempts = 0,
            LastAttemptAt = null,
        });
        await context.SaveChangesAsync();

        return (session, recipientId, handoffId);
    }
}
