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
/// S3's <see cref="SessionReminderDueJob"/> gate (PHASE4_DESIGN.md §7.2, P4-REMIND-01): the delivery
/// window actually delivers while the start is still ahead, cancels once it is not, never delivers a
/// stale/ineligible row, never double-delivers under concurrent workers, and dead-letters a handoff
/// that reaches its fifth execution attempt.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReminderDueJobTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// The §1.3 exit criterion in miniature: a reminder must be proven *delivered*, not only canceled.
    /// The delivering session starts 90 minutes out (still ahead of a due-now reminder); the canceling
    /// session's start has already passed by the time the job runs.
    /// </summary>
    [Fact]
    public async Task DeliversWhileTheStartIsAheadAndCancelsAtOrAfterStart_D082()
    {
        (Session deliverSession, UserId deliverRecipient, Guid deliverHandoffId) =
            await SeedEligibleDueHandoffAsync("deliver", leadMinutes: 90, durationMinutes: 60);

        (Session cancelSession, UserId cancelRecipient, Guid cancelHandoffId) =
            await SeedEligibleDueHandoffAsync("cancel", leadMinutes: 20, durationMinutes: 15);

        // Advance past the cancel session's start (but not the deliver session's) before the job runs.
        _factory.Clock.Advance(Duration.FromMinutes(25));
        Assert.True(_factory.Clock.GetCurrentInstant() < deliverSession.StartTime);
        Assert.True(_factory.Clock.GetCurrentInstant() >= cancelSession.StartTime);

        await RunJobAsync();

        SessionReminderHandoff delivered = await ReloadHandoffAsync(deliverHandoffId);
        Assert.Equal(SessionReminderHandoffState.Delivered, delivered.State);
        Assert.NotNull(delivered.DeliveredAt);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext context = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        bool notificationExists = await context.Notifications.AsNoTracking().AnyAsync(n =>
            n.SessionId == deliverSession.Id &&
            n.RecipientUserId == deliverRecipient &&
            n.Type == NotificationType.SessionReminder);
        Assert.True(notificationExists, "A Delivered handoff must leave exactly one SessionReminder notification row.");

        SessionReminderHandoff canceled = await ReloadHandoffAsync(cancelHandoffId);
        Assert.Equal(SessionReminderHandoffState.Canceled, canceled.State);
        Assert.Null(canceled.DeliveredAt);
        bool cancelNotificationExists = await context.Notifications.AsNoTracking().AnyAsync(n =>
            n.SessionId == cancelSession.Id &&
            n.RecipientUserId == cancelRecipient &&
            n.Type == NotificationType.SessionReminder);
        Assert.False(cancelNotificationExists);
    }

    /// <summary>
    /// Three independent reasons a discovered row must still deliver nothing: it changed state after
    /// discovery (stale), it is already resolved (Canceled), or the participant is no longer eligible
    /// (Removed) by the time the job's locked re-read happens.
    /// </summary>
    [Fact]
    public async Task StaleCanceledOrIneligibleRow_DeliversNothing_D082()
    {
        (Session staleSession, _, Guid staleHandoffId) =
            await SeedEligibleDueHandoffAsync("stale", leadMinutes: 90, durationMinutes: 60);
        (Session canceledSession, _, Guid alreadyCanceledHandoffId) =
            await SeedEligibleDueHandoffAsync("already-canceled", leadMinutes: 90, durationMinutes: 60);
        (Session ineligibleSession, UserId ineligibleRecipient, Guid ineligibleHandoffId) =
            await SeedEligibleDueHandoffAsync("ineligible", leadMinutes: 90, durationMinutes: 60);

        // Already resolved before discovery: excluded from the unlocked discovery query itself
        // (state = Pending only), so this row never reaches the job's per-session processing at all.
        await using (AsyncServiceScope cancelScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = cancelScope.ServiceProvider.GetRequiredService<AppDbContext>();
            SessionReminderHandoff alreadyCanceled = await context.SessionReminderHandoffs
                .SingleAsync(h => h.Id == alreadyCanceledHandoffId);
            alreadyCanceled.State = SessionReminderHandoffState.Canceled;
            await context.SaveChangesAsync();
        }

        // The participant leaves after the handoff was planned but before the job runs: still
        // Pending/due at discovery, but ineligible by the time the job's locked re-read evaluates it.
        await using (AsyncServiceScope removeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = removeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Session tracked = await context.Sessions.Include(s => s.Participations)
                .SingleAsync(s => s.Id == ineligibleSession.Id);
            tracked.Leave(ParticipantRef.ForUser(ineligibleRecipient), _factory.Clock);
            _ = tracked.DequeueEvents();
            await context.SaveChangesAsync();
        }

        // A version change strictly between this job's unlocked discovery and its locked re-read is
        // only reachable by racing the job against the very lock it is about to take: hold the
        // session parent externally so the job's unlocked discovery captures version 0, then bump the
        // handoff's version (simulating a concurrent replan) while the job is still blocked acquiring
        // the parent lock, then release it. The job's post-lock re-read must see the mismatch and skip.
        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM sessions WHERE id = {staleSession.Id.Value} FOR NO KEY UPDATE");

        await using AsyncServiceScope jobScope = _factory.Services.CreateAsyncScope();
        var job = jobScope.ServiceProvider.GetRequiredService<SessionReminderDueJob>();
        Task jobTask = job.ExecuteAsync(default);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(jobTask.IsCompleted, "The job must still be blocked acquiring the held session parent lock.");

        await using (AsyncServiceScope mutateScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = mutateScope.ServiceProvider.GetRequiredService<AppDbContext>();
            SessionReminderHandoff stale = await context.SessionReminderHandoffs.SingleAsync(h => h.Id == staleHandoffId);
            stale.Version++;
            await context.SaveChangesAsync();
        }

        await holderTransaction.RollbackAsync();
        await jobTask;

        SessionReminderHandoff staleAfter = await ReloadHandoffAsync(staleHandoffId);
        Assert.NotEqual(SessionReminderHandoffState.Delivered, staleAfter.State);
        Assert.Null(staleAfter.DeliveredAt);

        SessionReminderHandoff canceledAfter = await ReloadHandoffAsync(alreadyCanceledHandoffId);
        Assert.Equal(SessionReminderHandoffState.Canceled, canceledAfter.State);
        Assert.Null(canceledAfter.DeliveredAt);

        SessionReminderHandoff ineligibleAfter = await ReloadHandoffAsync(ineligibleHandoffId);
        Assert.Equal(SessionReminderHandoffState.Canceled, ineligibleAfter.State);
        Assert.Null(ineligibleAfter.DeliveredAt);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        int totalReminders = await verifyContext.Notifications.CountAsync(n =>
            n.Type == NotificationType.SessionReminder &&
            (n.SessionId == staleSession.Id || n.SessionId == canceledSession.Id || n.SessionId == ineligibleSession.Id));
        Assert.Equal(0, totalReminders);
    }

    /// <summary>
    /// F1 regression: a replan that lands strictly between the unlocked discovery query and this job's
    /// locked re-read must never be delivered early, even if it happens to leave the row's
    /// <c>version</c> looking unchanged from what discovery captured. The replan is simulated by
    /// mutating only <c>due_at</c> into the future while the job is blocked acquiring the session
    /// parent lock (mirroring the verifier's scenario) — the job's post-lock re-read must see the
    /// now-future due_at and leave the row exactly Pending with that future due_at, not deliver or
    /// cancel it.
    /// </summary>
    [Fact]
    public async Task ReplanBetweenDiscoveryAndLock_LeavesTheHandoffPendingWithItsFutureDueAt_D082()
    {
        (Session session, UserId recipient, Guid handoffId) =
            await SeedEligibleDueHandoffAsync("replan-race", leadMinutes: 90, durationMinutes: 60);

        Instant futureDueAt = _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(30);

        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM sessions WHERE id = {session.Id.Value} FOR NO KEY UPDATE");

        await using AsyncServiceScope jobScope = _factory.Services.CreateAsyncScope();
        var job = jobScope.ServiceProvider.GetRequiredService<SessionReminderDueJob>();
        Task jobTask = job.ExecuteAsync(default);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(jobTask.IsCompleted, "The job must still be blocked acquiring the held session parent lock.");

        await using (AsyncServiceScope mutateScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = mutateScope.ServiceProvider.GetRequiredService<AppDbContext>();
            SessionReminderHandoff replanned = await context.SessionReminderHandoffs.SingleAsync(h => h.Id == handoffId);
            replanned.DueAt = futureDueAt;
            await context.SaveChangesAsync();
        }

        await holderTransaction.RollbackAsync();
        await jobTask;

        SessionReminderHandoff after = await ReloadHandoffAsync(handoffId);
        Assert.Equal(SessionReminderHandoffState.Pending, after.State);
        Assert.Equal(futureDueAt, after.DueAt);
        Assert.Null(after.DeliveredAt);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        bool notificationExists = await verifyContext.Notifications.AnyAsync(n =>
            n.SessionId == session.Id && n.RecipientUserId == recipient && n.Type == NotificationType.SessionReminder);
        Assert.False(notificationExists, "A due_at that moved into the future between discovery and the lock must never deliver early.");
    }

    /// <summary>
    /// The parent-lock-plus-version-recheck contract is what makes exactly-once delivery hold under
    /// two workers racing the same discovered candidate, not a special case in the job itself.
    /// </summary>
    [Fact]
    public async Task ConcurrentWorkers_CreateExactlyOneReminderNotification_D082()
    {
        (Session session, UserId recipient, Guid handoffId) =
            await SeedEligibleDueHandoffAsync("concurrent", leadMinutes: 90, durationMinutes: 60);

        await using AsyncServiceScope scopeA = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope scopeB = _factory.Services.CreateAsyncScope();
        SessionReminderDueJob jobA = scopeA.ServiceProvider.GetRequiredService<SessionReminderDueJob>();
        SessionReminderDueJob jobB = scopeB.ServiceProvider.GetRequiredService<SessionReminderDueJob>();

        await Task.WhenAll(jobA.ExecuteAsync(default), jobB.ExecuteAsync(default));

        SessionReminderHandoff handoff = await ReloadHandoffAsync(handoffId);
        Assert.Equal(SessionReminderHandoffState.Delivered, handoff.State);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext context = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        int notificationCount = await context.Notifications.CountAsync(n =>
            n.SessionId == session.Id && n.RecipientUserId == recipient && n.Type == NotificationType.SessionReminder);
        Assert.Equal(1, notificationCount);
    }

    /// <summary>
    /// F3 regression: attempts must be reachable through genuine, durably-recorded execution attempts
    /// (not a hand-written <c>Attempts = 4</c>), and the fifth recorded attempt must still attempt
    /// delivery normally — it dead-letters here because that attempt also resolves to "not
    /// deliverable" (the participant left after the handoff was planned), the same outcome a single
    /// attempt would reach on its own; attempts alone never forecloses a real delivery attempt.
    /// </summary>
    [Fact]
    public async Task FifthFailureDeadLettersTheHandoff_A066()
    {
        (Session session, UserId recipient, Guid handoffId) =
            await SeedEligibleDueHandoffAsync("dead-letter", leadMinutes: 90, durationMinutes: 60);

        // The participant leaves after the handoff was planned: still Pending/due, but no longer
        // deliverable — every real attempt below resolves to "not deliverable", the same branch a
        // single successful attempt would take straight to Canceled.
        await using (AsyncServiceScope leaveScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = leaveScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Session tracked = await context.Sessions.Include(s => s.Participations).SingleAsync(s => s.Id == session.Id);
            tracked.Leave(ParticipantRef.ForUser(recipient), _factory.Clock);
            _ = tracked.DequeueEvents();
            await context.SaveChangesAsync();
        }

        Instant now = _factory.Clock.GetCurrentInstant();

        // Four genuine forced crashes, each mirroring exactly what SessionReminderDueJob does for one
        // candidate: record the attempt in its own committed statement first (so it survives the crash
        // below), then lock/read and fail before commit. This proves attempts is durably reachable in
        // production instead of being erased by rollback (the F3 bug) — the counter really does reach
        // 4 through real recorded attempts, not a hand-written field set.
        for (int crash = 0; crash < 4; crash++)
        {
            await using AsyncServiceScope crashScope = _factory.Services.CreateAsyncScope();
            var unitOfWork = crashScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var sessionLocks = crashScope.ServiceProvider.GetRequiredService<ISessionLockRepository>();
            var candidateStore = crashScope.ServiceProvider.GetRequiredService<SessionDueCandidateStore>();

            int currentVersion;
            await using (AsyncServiceScope readScope = _factory.Services.CreateAsyncScope())
            {
                AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
                currentVersion = await context.SessionReminderHandoffs.AsNoTracking()
                    .Where(h => h.Id == handoffId)
                    .Select(h => h.Version)
                    .SingleAsync();
            }

            await candidateStore.RecordAttemptAsync(handoffId, currentVersion, now, default);

            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteAsync<bool>(
                async ct =>
                {
                    await sessionLocks.LockSessionsAsync([session.Id], ct);
                    await candidateStore.LockSessionHandoffsAsync(session.Id, ct);
                    _ = await candidateStore.FindHandoffAsync(handoffId, ct)
                        ?? throw new InvalidOperationException("Seed handoff missing.");
                    throw new InvalidOperationException("Simulated crash before commit.");
                },
                default));
        }

        SessionReminderHandoff beforeFifth = await ReloadHandoffAsync(handoffId);
        Assert.Equal(4, beforeFifth.Attempts);
        Assert.Equal(SessionReminderHandoffState.Pending, beforeFifth.State);

        await RunJobAsync();

        SessionReminderHandoff afterFifth = await ReloadHandoffAsync(handoffId);
        Assert.Equal(SessionReminderHandoffState.Failed, afterFifth.State);
        Assert.Equal(5, afterFifth.Attempts);
        Assert.Null(afterFifth.DeliveredAt);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        bool notificationExists = await verifyContext.Notifications.AnyAsync(n =>
            n.SessionId == session.Id && n.RecipientUserId == recipient && n.Type == NotificationType.SessionReminder);
        Assert.False(notificationExists, "A dead-lettered attempt must never write the reminder notification.");
    }

    private async Task<(Session Session, UserId Recipient, Guid HandoffId)> SeedEligibleDueHandoffAsync(
        string label,
        int leadMinutes,
        int durationMinutes)
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"reminder-{label}-organizer@example.test");
        UserId recipientId = await _factory.SeedUserAsync(email: $"reminder-{label}-recipient@example.test");

        Session session = CreateSession(organizerId, leadMinutes, durationMinutes);
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

    private async Task RunJobAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<SessionReminderDueJob>();
        await job.ExecuteAsync(default);
    }

    private async Task<SessionReminderHandoff> ReloadHandoffAsync(Guid handoffId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.SessionReminderHandoffs.AsNoTracking().SingleAsync(h => h.Id == handoffId);
    }

    private Session CreateSession(UserId organizerId, int leadMinutes, int durationMinutes)
    {
        SessionDetails details = new(
            SessionTitle.Create("Reminder job test session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(leadMinutes),
            Duration15.FromMinutes(durationMinutes),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: true);
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        _ = session.DequeueEvents();
        return session;
    }
}
