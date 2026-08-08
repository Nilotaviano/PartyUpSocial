using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Jobs;

/// <summary>
/// The poll analogue of <see cref="SessionDueCandidateStore"/> (PHASE6_DESIGN.md §3): every
/// discovery query here is unlocked, database-ordered, and bounded to <see cref="BatchSize"/> rows
/// per tick, targeting the <c>ix_polls_status_deadline_id</c> index so <c>status</c> is the leading
/// predicate — a poll a tick processes moves out of the matching status (or, for the deadline
/// notice, out of the unlocked-notice window) and simply stops matching on the next tick.
/// </summary>
public sealed class PollDueCandidateStore(AppDbContext context)
{
    private const int BatchSize = 500;

    /// <summary>
    /// D-079/D-088's bounded due-sweep predicate: a <c>Polling</c> poll whose deadline has passed, or
    /// a <c>Closed</c> poll whose 48-hour selection window has ended — <see cref="Poll.ApplyDueTransitions"/>
    /// (via <see cref="Application.Polls.PollTransitionUseCase"/>) handles both in one pass, so a poll
    /// left unswept past both boundaries closes and then expires the same tick it is picked up.
    /// </summary>
    public async Task<IReadOnlyList<PollId>> GetDueSweepCandidatesAsync(Instant now, CancellationToken cancellationToken)
    {
        List<Guid> ids = await context.Database.SqlQuery<Guid>($"""
            SELECT id
            FROM polls
            WHERE (status = 0 AND deadline <= {now})
               OR (status = 1 AND selection_window_end <= {now})
            ORDER BY deadline, id
            LIMIT {BatchSize}
            """).ToListAsync(cancellationToken);

        return [.. ids.Select(PollId.From)];
    }

    /// <summary>
    /// D-120's unlocked discovery: a <c>Polling</c> poll whose deadline is within the next hour but
    /// has not yet passed — the exact window <see cref="Domain.Notifications.PollDeadlineNotice.ShouldSend"/>
    /// re-checks under the poll's own lock, because a poll can leave <c>Polling</c> or its deadline
    /// window can close in the unlocked gap between this query and that lock.
    /// </summary>
    public async Task<IReadOnlyList<PollId>> GetDeadlineApproachingCandidatesAsync(Instant now, CancellationToken cancellationToken)
    {
        List<Guid> ids = await context.Database.SqlQuery<Guid>($"""
            SELECT id
            FROM polls
            WHERE status = 0
              AND deadline - interval '1 hour' <= {now}
              AND deadline > {now}
            ORDER BY deadline, id
            LIMIT {BatchSize}
            """).ToListAsync(cancellationToken);

        return [.. ids.Select(PollId.From)];
    }

    /// <summary>
    /// D-120's duplicate suppression: the partial-index inference form of <c>ON CONFLICT</c>, naming
    /// the exact predicate <c>ux_notifications_poll_deadline_once</c> carries (<c>type = 15</c>, the
    /// <see cref="Domain.Notifications.NotificationType.PollDeadlineApproaching"/> ordinal). A second
    /// worker's insert for the same (poll, recipient) pair is a silent no-op, never a caught
    /// unique-violation — the double-run proof this buys is per recipient, which is every recipient
    /// the batch computes, so the whole notice batch provably emits once.
    /// </summary>
    public Task InsertDeadlineApproachingNotificationIfAbsentAsync(
        PollId pollId,
        UserId recipientUserId,
        Instant now,
        CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications
                (id, recipient_user_id, type, session_id, poll_id, actor_user_id, option_start_time, created_at, read_at)
            VALUES
                ({Guid.CreateVersion7()}, {recipientUserId.Value}, 15, NULL, {pollId.Value}, NULL, NULL, {now}, NULL)
            ON CONFLICT (poll_id, recipient_user_id) WHERE type = 15 DO NOTHING
            """, cancellationToken);
}
