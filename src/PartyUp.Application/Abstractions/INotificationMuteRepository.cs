using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Abstractions;

// S1 (PHASE7_DESIGN.md §1) needs only the §11 step 5 purge; S4 extends this port with the mute write
// path (Add / RemoveForTarget) plus the read PollDetailResponse.muted needs, per PHASE7_DESIGN.md §4.
public interface INotificationMuteRepository
{
    /// <summary>§11 step 5: every mute row the user holds, session- or poll-targeted alike.</summary>
    Task DeleteForUserAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a mute row via <c>INSERT ... ON CONFLICT DO NOTHING</c> against the target's partial
    /// unique index (§1) — a DB constraint, never an app check (ARCHITECTURE §3) — so muting twice is
    /// idempotent even under a race.
    /// </summary>
    Task AddAsync(NotificationMute mute, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the mute row for exactly one of <paramref name="sessionId"/>/<paramref name="pollId"/>
    /// (the other is <see langword="null"/>) — idempotent no-op when none exists.
    /// </summary>
    Task RemoveForTargetAsync(
        UserId userId,
        SessionId? sessionId,
        PollId? pollId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the user currently holds a mute on exactly this session or poll target — the
    /// <c>PollDetailResponse.muted</c>/mute-toggle read, not part of the write path proper.
    /// </summary>
    Task<bool> ExistsForTargetAsync(
        UserId userId,
        SessionId? sessionId,
        PollId? pollId,
        CancellationToken cancellationToken);
}
