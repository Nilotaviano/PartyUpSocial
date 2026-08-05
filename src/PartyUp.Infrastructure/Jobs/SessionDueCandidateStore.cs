using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Jobs;

/// <summary>
/// The raw-SQL boundary the S3 jobs share (PHASE4_DESIGN.md §4.1, §7.2). Every discovery query here is
/// unlocked and keyset-paged, bounded to <see cref="BatchSize"/> rows per tick — no offset paging, no
/// EF-translated arithmetic over <c>start_time</c>. Locking and the reminder duplicate-suppression
/// insert are also raw SQL, because both target rows the mapped LINQ surface does not expose a lock or
/// <c>ON CONFLICT</c> path for.
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
    /// §7.2's unlocked reminder discovery: <c>state = Pending (0) AND due_at &lt;= now</c>, keyset-paged on
    /// <c>(due_at, id)</c>. The <c>Version</c> captured here is what the job later compares against the
    /// freshly locked row to detect a stale candidate (PHASE4_DESIGN.md §7.2's "version/state unchanged
    /// since discovery" clause) — this query itself takes no lock.
    /// </summary>
    public async Task<IReadOnlyList<DueReminderCandidate>> GetDueReminderCandidatesAsync(
        Instant now,
        CancellationToken cancellationToken)
    {
        List<Guid> orderedIds = await context.Database.SqlQuery<Guid>($"""
            SELECT id
            FROM session_reminder_handoffs
            WHERE state = 0 AND due_at <= {now}
            ORDER BY due_at, id
            LIMIT {BatchSize}
            """).ToListAsync(cancellationToken);

        if (orderedIds.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, (SessionId SessionId, int Version)> byId = await context.SessionReminderHandoffs
            .Where(handoff => orderedIds.Contains(handoff.Id))
            .Select(handoff => new { handoff.Id, handoff.SessionId, handoff.Version })
            .ToDictionaryAsync(row => row.Id, row => (row.SessionId, row.Version), cancellationToken);

        // Preserves the discovery query's (due_at, id) order; a row vanishing between the two
        // queries (theoretically possible, never observed) is simply dropped rather than throwing.
        return [.. orderedIds
            .Where(byId.ContainsKey)
            .Select(id => new DueReminderCandidate(id, byId[id].SessionId, byId[id].Version))];
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
/// rule), and the version discovered so the locked re-read can detect staleness.
/// </summary>
public sealed record DueReminderCandidate(Guid HandoffId, SessionId SessionId, int Version);
