using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// S3's dispatch-page adapter (PHASE7_DESIGN.md §3). The <c>notifications</c> table is the whole
/// outbox — this port only pages it and flips its <c>dispatch_state</c> shadow column through the two
/// shared, idempotent SQL transitions the design names; nothing here re-derives state from timestamps
/// (ARCHITECTURE §3's materialized-status invariant).
/// </summary>
public interface INotificationDispatchRepository
{
    /// <summary>
    /// Every <c>dispatch_state = 0</c> row, ordered <c>(created_at, id)</c> — no time bound in the
    /// predicate, so a Pending row is reached on every tick until it transitions, per the design's
    /// "leaves the index" contract.
    /// </summary>
    Task<IReadOnlyList<NotificationId>> PagePendingIdsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// <c>UPDATE ... WHERE dispatch_state = 0</c>: idempotent, single-statement, and the whole terminal
    /// transition for the lateness/mute/unreachable branches. Returns whether this call was the one
    /// that flipped it (never load-bearing for correctness — every caller already returns after this).
    /// </summary>
    Task<bool> TryMarkSkippedAsync(NotificationId id, CancellationToken cancellationToken);

    /// <summary>
    /// The single UPDATE that is the whole "every live subscription carries a send-log row" coverage
    /// rule: a <c>NOT EXISTS</c> subquery evaluated atomically inside the same statement, so it is what
    /// both the zero-subscription case (trivially true) and the crash-recovery case (already covered by
    /// prior send-log rows, regardless of their outcome) resolve through — never a separate read then
    /// write.
    /// </summary>
    Task<bool> TryCompleteDispatchIfCoveredAsync(
        NotificationId id,
        UserId recipientUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The dispatch-time 3-arm mute join (§3): <c>poll_id</c> direct, <c>session_id</c> direct, or
    /// <c>session_id</c> reached through <c>sessions.origin_poll_id</c> (the D-160 linkage a
    /// finalization re-parents onto). Callers never call this for <c>SessionReminder</c> (type 12) —
    /// the same single exemption the write path carries (D-090, <c>ReminderRules</c>).
    /// </summary>
    Task<bool> IsMutedAsync(
        UserId recipientUserId,
        SessionId? sessionId,
        PollId? pollId,
        CancellationToken cancellationToken);
}
