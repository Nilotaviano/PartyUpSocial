using NodaTime;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Users.DeletionExecution;

/// <summary>
/// A class of rows the executor marks for deletion rather than mutating (DOMAIN_MODEL §11 step 5):
/// none of them is an aggregate with invariants to uphold, so the domain names them and the
/// application layer deletes them.
/// </summary>
// Values are carried across layer boundaries: append only, never renumber.
public enum UserDataScope
{
    /// <summary>Series, one-time blocks, occurrence skips — the series cascade of §12-21.</summary>
    Availability = 0,

    NotificationMutes = 1,

    PushSubscriptions = 2,

    /// <summary>Notification rows addressed to the user; they never held names to begin with (§9).</summary>
    Notifications = 3,

    /// <summary>AVATARS_DESIGN.md §3.4: every <c>AvatarSource</c> row this user captured.</summary>
    AvatarSources = 4,
}

/// <summary>
/// A-017: <c>(user_id, executed_at)</c>, appended at grace-expiry execution. Pseudonymous by design —
/// the id stays FK-stable to the tombstone, which is what makes restore-replay work.
/// </summary>
public sealed record DeletionJournalEntry(UserId UserId, Instant ExecutedAt);

/// <summary>
/// What one deletion execution did (DOMAIN_MODEL §11). Sessions, polls and join requests are already
/// mutated on the aggregates that were passed in; friendships, blocks,
/// <see cref="DeletedGroups"/> and <see cref="PurgedData"/> leave as instructions, because deleting
/// rows is the caller's job. The subject's own membership rows in others' groups (D-209) are already
/// removed on the <see cref="FriendGroup"/> aggregates the caller passed in — the same pattern
/// sessions/polls use — so there is no separate list for those; only the owned groups the caller must
/// hard-delete are named here. Re-running over a tombstone reports empty change lists and the
/// <em>same</em> <see cref="Journal"/> — the original execution instant, never a fresh one.
/// </summary>
public sealed record DeletionExecutionResult(
    IReadOnlyList<SessionId> CanceledSessions,
    IReadOnlyList<PollId> ExpiredPolls,
    IReadOnlyList<SessionId> RemovedFromSessions,
    IReadOnlyList<PollId> RemovedFromPolls,
    IReadOnlyList<JoinRequestId> LapsedJoinRequests,
    IReadOnlyList<SessionReminderCanceled> CanceledReminders,
    IReadOnlyList<Friendship> DeletedFriendships,
    IReadOnlyList<Block> DeletedBlocks,
    IReadOnlyList<UserDataScope> PurgedData,
    UsernameRegistryEntry? ReservedUsername,
    DeletionJournalEntry Journal,
    IReadOnlyList<FriendGroupId> DeletedGroups)
{
    /// <summary>
    /// The §11 steps 1–3 half of this result, in the shape the D-180 content-removal action produces
    /// on its own — so the applier's removal tail has exactly one implementation and the deletion
    /// overload delegates to it instead of duplicating the reminder-cancel loop.
    /// </summary>
    public ContentRemovalResult ContentRemoval => new(
        CanceledSessions,
        ExpiredPolls,
        RemovedFromSessions,
        RemovedFromPolls,
        LapsedJoinRequests,
        CanceledReminders);
}
