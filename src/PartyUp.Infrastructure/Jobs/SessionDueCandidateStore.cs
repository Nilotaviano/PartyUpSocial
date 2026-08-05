using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Jobs;

/// <summary>
/// The raw-SQL boundary the S3 jobs share (PHASE4_DESIGN.md §4.1, §7.2). Every discovery query here is
/// unlocked and bounded to <see cref="BatchSize"/> rows per tick via a single <c>ORDER BY … LIMIT</c> —
/// no offset paging, no EF-translated arithmetic over <c>start_time</c>. There is no carried cursor
/// between ticks: a row a tick processes moves out of terminal/resolved state (or, for reminders,
/// Delivered/Canceled/Failed), so it simply stops matching the predicate on the next tick and progress
/// is made by the predicate shrinking the candidate set, not by paging state. Locking and the reminder
/// duplicate-suppression insert are also raw SQL, because both target rows the mapped LINQ surface does
/// not expose a lock or <c>ON CONFLICT</c> path for.
/// </summary>
public sealed class SessionDueCandidateStore(AppDbContext context)
{
    private const int BatchSize = 500;
    private const int DueSweepWindowMinutes = 1440;

    /// <summary>
    /// §4.1's bounded due-sweep predicate: materialized status <c>Confirmed</c>/<c>Unconfirmed</c> (0/1),
    /// <c>start_time</c> inside the 1440-minute window the duration cap guarantees is complete, and the
    /// exact <c>start_time + make_interval(mins => duration_minutes) &lt;= now()</c> end-time test. Ordered
    /// and capped so a session already reprocessed to a terminal status by the same or a later tick simply
    /// falls out of the next scan.
    /// </summary>
    public async Task<IReadOnlyList<SessionId>> GetDueSweepCandidatesAsync(
        Instant now,
        CancellationToken cancellationToken)
    {
        List<Guid> ids = await context.Database.SqlQuery<Guid>($"""
            SELECT id
            FROM sessions
            WHERE status IN (0, 1)
              AND start_time <= {now}
              AND start_time >= {now} - make_interval(mins => {DueSweepWindowMinutes})
              AND start_time + make_interval(mins => duration_minutes) <= {now}
            ORDER BY start_time, id
            LIMIT {BatchSize}
            """).ToListAsync(cancellationToken);

        return [.. ids.Select(SessionId.From)];
    }

    /// <summary>
    /// §7.2's unlocked reminder discovery: <c>state = Pending (0) AND due_at &lt;= now</c>, ordered and
    /// capped on <c>(due_at, id)</c> via a single <c>ORDER BY … LIMIT</c> — not a carried keyset cursor;
    /// a row a tick delivers/cancels/dead-letters simply stops matching <c>state = Pending</c> on the
    /// next tick. This is exactly ONE raw statement, so the <c>Version</c> and <c>DueAt</c>
    /// captured here come from the same row snapshot the <c>due_at &lt;= now</c> predicate matched — a
    /// second, separately-timed query reading <c>Version</c> could observe a replan that committed
    /// between the two statements and hand the job a post-replan version that looks unchanged at the
    /// locked re-read even though the row's real due instant had moved into the future. The job still
    /// re-checks both <c>Version</c>/<c>State</c> and <c>DueAt</c> under the lock (PHASE4_DESIGN.md
    /// §7.2's "version/state unchanged since discovery" clause plus the due-at recheck) because this
    /// query itself takes no lock and a replan can still land in the gap between discovery and the lock.
    /// </summary>
    public async Task<IReadOnlyList<DueReminderCandidate>> GetDueReminderCandidatesAsync(
        Instant now,
        CancellationToken cancellationToken)
    {
        List<DueReminderDiscoveryRow> rows = await context.Database.SqlQuery<DueReminderDiscoveryRow>($"""
            SELECT id, session_id, version, due_at
            FROM session_reminder_handoffs
            WHERE state = 0 AND due_at <= {now}
            ORDER BY due_at, id
            LIMIT {BatchSize}
            """).ToListAsync(cancellationToken);

        return [.. rows.Select(row =>
            new DueReminderCandidate(row.Id, SessionId.From(row.SessionId), row.Version, row.DueAt))];
    }

    private sealed record DueReminderDiscoveryRow(Guid Id, Guid SessionId, int Version, Instant DueAt);

    /// <summary>
    /// §7.2's durable attempt bookkeeping: a single autocommitted statement, issued with no ambient
    /// transaction, so the increment survives even when the caller's later per-session transaction
    /// rolls back or crashes (F3: previously <c>Attempts++</c> shared that transaction and rollback
    /// erased it, so a Pending row was never observably seen above zero attempts). Guarded on
    /// <c>state = 0 AND version = expectedVersion</c> so an already-resolved or already-replanned row
    /// (discovered stale) does not have a stray attempt recorded against a candidate it no longer is.
    /// Returns the freshly committed attempts count, or <c>null</c> when the guard did not match.
    /// </summary>
    public async Task<int?> RecordAttemptAsync(
        Guid handoffId,
        int expectedVersion,
        Instant now,
        CancellationToken cancellationToken)
    {
        int rowsAffected = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE session_reminder_handoffs
            SET attempts = attempts + 1, last_attempt_at = {now}
            WHERE id = {handoffId} AND state = 0 AND version = {expectedVersion}
            """, cancellationToken);

        if (rowsAffected == 0)
        {
            return null;
        }

        List<int> attempts = await context.Database.SqlQuery<int>($"""
            SELECT attempts
            FROM session_reminder_handoffs
            WHERE id = {handoffId}
            """).ToListAsync(cancellationToken);

        return attempts.Count == 1 ? attempts[0] : null;
    }

    /// <summary>
    /// A-068's declared <c>session_reminder_handoffs -> ORDER BY due_at, id</c>, always issued within an
    /// already-locked session parent (§6.1 step 4 / §7.2): one statement, database-ordered.
    /// </summary>
    public Task LockSessionHandoffsAsync(SessionId sessionId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM session_reminder_handoffs
            WHERE session_id = {sessionId.Value}
            ORDER BY due_at, id
            FOR NO KEY UPDATE
            """, cancellationToken);

    /// <summary>
    /// The tracked handoff row the job mutates. Callers must already hold the session parent and
    /// handoff-table locks (<see cref="LockSessionHandoffsAsync"/>) before calling this.
    /// </summary>
    public Task<SessionReminderHandoff?> FindHandoffAsync(Guid handoffId, CancellationToken cancellationToken) =>
        context.SessionReminderHandoffs.SingleOrDefaultAsync(
            handoff => handoff.Id == handoffId,
            cancellationToken);

    /// <summary>
    /// §7.2's duplicate suppression: the partial-index inference form of <c>ON CONFLICT</c>, naming the
    /// exact predicate <c>ux_notifications_session_reminder_once</c> carries (<c>type = 12</c>, the
    /// <see cref="Domain.Notifications.NotificationType.SessionReminder"/> ordinal). The caller marks the
    /// handoff <c>Delivered</c> in the same transaction regardless of how many rows this affected — a
    /// second worker's insert is a silent no-op, never a caught unique-violation.
    /// </summary>
    public Task InsertReminderNotificationIfAbsentAsync(
        Guid handoffId,
        UserId recipientUserId,
        SessionId sessionId,
        Instant now,
        CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications
                (id, recipient_user_id, type, session_id, poll_id, actor_user_id, option_start_time, created_at, read_at, reminder_handoff_id)
            VALUES
                ({Guid.CreateVersion7()}, {recipientUserId.Value}, 12, {sessionId.Value}, NULL, NULL, NULL, {now}, NULL, {handoffId})
            ON CONFLICT (reminder_handoff_id) WHERE type = 12 DO NOTHING
            """, cancellationToken);
}

/// <summary>
/// One row <see cref="SessionDueCandidateStore.GetDueReminderCandidatesAsync"/> discovered: the handoff
/// id, the session it belongs to (jobs group candidates by this for the one-transaction-per-session
/// rule), the version discovered so the locked re-read can detect staleness, and the due instant
/// discovered so the locked re-read can also detect a due instant that moved into the future.
/// </summary>
public sealed record DueReminderCandidate(Guid HandoffId, SessionId SessionId, int Version, Instant DueAt);
