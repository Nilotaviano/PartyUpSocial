using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Abstractions;

public interface INotificationRepository
{
    void Add(Notification notification);

    /// <summary>S3: the dispatch job's per-row read, untracked — the job never writes through the aggregate itself.</summary>
    Task<Notification?> FindAsync(NotificationId id, CancellationToken cancellationToken);

    /// <summary>
    /// S4's <c>MarkNotificationRead</c> read: tracked, owned by <paramref name="recipientId"/> only —
    /// an unknown or another user's id must be indistinguishable from not found (fail closed).
    /// </summary>
    Task<Notification?> FindOwnedAsync(
        NotificationId id,
        UserId recipientId,
        CancellationToken cancellationToken);

    /// <summary>
    /// S4's <c>MarkAllNotificationsRead</c> read: every row unread for the recipient at call time,
    /// tracked so <see cref="Notification.MarkRead"/> — the idempotent first-read-wins domain mutator
    /// — is what commits, never a raw column write.
    /// </summary>
    Task<IReadOnlyList<Notification>> ListUnreadForUpdateAsync(
        UserId recipientId,
        CancellationToken cancellationToken);

    Task AddSteamMatchIfAbsentAsync(
        Notification notification,
        CancellationToken cancellationToken);

    Task DeleteForRecipientAsync(
        UserId recipientUserId,
        CancellationToken cancellationToken);
}
