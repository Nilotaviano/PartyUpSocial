using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// S3's dispatch-page adapter (PHASE7_DESIGN.md §3). Every transition is one interpolated SQL
/// statement — never a tracked-entity read/mutate/<c>SaveChanges</c> round trip — so each is its own
/// autocommit unit and, for the two conditional updates, atomically evaluates the predicate it commits
/// against (ARCHITECTURE §3's materialized-status invariant: only these shared functions ever write
/// <c>dispatch_state</c>).
/// </summary>
public sealed class NotificationDispatchRepository(AppDbContext context) : INotificationDispatchRepository
{
    public async Task<IReadOnlyList<NotificationId>> PagePendingIdsAsync(int limit, CancellationToken cancellationToken)
    {
        List<Guid> ids = await context.Database.SqlQuery<Guid>($"""
            SELECT id FROM notifications
            WHERE dispatch_state = 0
            ORDER BY created_at, id
            LIMIT {limit}
            """).ToListAsync(cancellationToken);

        return [.. ids.Select(NotificationId.From)];
    }

    public async Task<bool> TryMarkSkippedAsync(NotificationId id, CancellationToken cancellationToken)
    {
        int affected = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notifications SET dispatch_state = 2
            WHERE id = {id.Value} AND dispatch_state = 0
            """, cancellationToken);
        return affected > 0;
    }

    public async Task<bool> TryCompleteDispatchIfCoveredAsync(
        NotificationId id,
        UserId recipientUserId,
        CancellationToken cancellationToken)
    {
        int affected = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notifications SET dispatch_state = 1
            WHERE id = {id.Value}
              AND dispatch_state = 0
              AND NOT EXISTS (
                  SELECT 1 FROM push_subscriptions ps
                  WHERE ps.user_id = {recipientUserId.Value} AND ps.revoked_at IS NULL
                    AND NOT EXISTS (
                        SELECT 1 FROM notification_push_sends nps
                        WHERE nps.notification_id = {id.Value} AND nps.push_subscription_id = ps.id
                    )
              )
            """, cancellationToken);
        return affected > 0;
    }

    public async Task<bool> IsMutedAsync(
        UserId recipientUserId,
        SessionId? sessionId,
        PollId? pollId,
        CancellationToken cancellationToken)
    {
        Guid? sessionGuid = sessionId?.Value;
        Guid? pollGuid = pollId?.Value;

        List<bool> rows = await context.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM notification_mutes m
                WHERE m.user_id = {recipientUserId.Value}
                  AND (
                      ({pollGuid}::uuid IS NOT NULL AND m.poll_id = {pollGuid}::uuid)
                      OR ({sessionGuid}::uuid IS NOT NULL AND m.session_id = {sessionGuid}::uuid)
                      OR ({pollGuid}::uuid IS NOT NULL AND m.session_id IN (
                          SELECT id FROM sessions WHERE origin_poll_id = {pollGuid}::uuid
                      ))
                  )
            ) AS "Value"
            """).ToListAsync(cancellationToken);

        return rows.Count > 0 && rows[0];
    }
}
