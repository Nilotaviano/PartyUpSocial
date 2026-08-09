using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// PHASE7_DESIGN.md §4's notification-list read: A-072 keyset paging (<c>created_at</c> desc, <c>id</c>
/// desc) over the shipped <c>ix_notifications_recipient_created</c>/<c>ix_notifications_unread</c>
/// indexes (§1), under the two distinct <see cref="SessionListScope"/> members
/// <see cref="SessionListScope.NotificationsAll"/>/<see cref="SessionListScope.NotificationsUnread"/>.
/// Rows are hydrated <see cref="Notification"/> aggregates — masking and subject-access re-resolution
/// happen downstream, in the shipped S2 <c>NotificationReadUseCases</c>, never here.
/// </summary>
public interface INotificationReadRepository
{
    Task<SessionPage<Notification>> ListAsync(
        UserId recipientId,
        bool unreadOnly,
        SessionListCursor? cursor,
        int pageSize,
        SessionListScope scope,
        CancellationToken cancellationToken);

    Task<int> CountUnreadAsync(UserId recipientId, CancellationToken cancellationToken);
}
