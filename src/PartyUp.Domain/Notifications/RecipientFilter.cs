using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Notifications;

/// <summary>
/// The two write-time suppressions every §10 recipient set passes through: the recipient's own mutes
/// (§9, D-090/D-113) and the deletion executor's exclusion of the user being deleted (§11 step 5 —
/// "the deleting user receives no notifications from steps 1–3").
/// </summary>
/// <remarks>
/// Both are recipient-side, so they compose with the per-event carve-outs in §10 rather than
/// replacing them: a rejected requester and a waitlist-rejected participant are still explicit
/// targets, and they are still dropped here if they muted the target.
/// </remarks>
public sealed class RecipientFilter
{
    /// <summary>No mutes, nobody excluded.</summary>
    public static readonly RecipientFilter None = new(MuteSet.Empty, null);

    private RecipientFilter(MuteSet mutes, UserId? excludedUserId)
    {
        Mutes = mutes;
        ExcludedUserId = excludedUserId;
    }

    public MuteSet Mutes { get; }

    /// <summary>The user whose deletion is executing, if any (§11).</summary>
    public UserId? ExcludedUserId { get; }

    public static RecipientFilter Create(MuteSet mutes)
    {
        ArgumentNullException.ThrowIfNull(mutes);

        return new RecipientFilter(mutes, null);
    }

    public static RecipientFilter Create(MuteSet mutes, UserId? excludedUserId)
    {
        ArgumentNullException.ThrowIfNull(mutes);

        return new RecipientFilter(mutes, excludedUserId);
    }

    /// <summary>The deletion executor's hook: everything it triggers is computed without this user.</summary>
    public RecipientFilter Excluding(UserId userId) => new(Mutes, userId);

    public bool Allows(UserId userId, NotificationSubject subject) =>
        ExcludedUserId != userId && !Mutes.IsMuted(userId, subject.SessionId, subject.PollId);

    /// <summary>
    /// The reminder is the sole mute exception (§9, D-090): only the deletion exclusion applies, and
    /// whether it fires at all is decided by <c>ReminderChoice</c>/<c>ReminderDefault</c>.
    /// </summary>
    public bool AllowsReminder(UserId userId) => ExcludedUserId != userId;
}
