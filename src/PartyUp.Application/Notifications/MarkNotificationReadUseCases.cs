using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Notifications;

/// <summary>
/// PHASE7_DESIGN.md §4's <c>MarkNotificationRead</c>: idempotent (the domain
/// <see cref="Notification.MarkRead"/> mutator keeps the first <c>ReadAt</c>), fail-closed on an
/// unknown or another user's id (<see cref="INotificationRepository.FindOwnedAsync"/> already scopes
/// to the recipient).
/// </summary>
public sealed class MarkNotificationReadUseCase(
    INotificationRepository notifications,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<bool> ExecuteAsync(
        NotificationId id,
        UserId recipientId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                Notification? notification = await notifications.FindOwnedAsync(id, recipientId, ct);
                if (notification is null)
                {
                    return false;
                }

                notification.MarkRead(clock);
                return true;
            },
            cancellationToken);
}

/// <summary>
/// PHASE7_DESIGN.md §4's <c>MarkAllNotificationsRead</c>: idempotent — marks every row unread for the
/// recipient at call time, through the same domain mutator, never a raw column write.
/// </summary>
public sealed class MarkAllNotificationsReadUseCase(
    INotificationRepository notifications,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task ExecuteAsync(UserId recipientId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                IReadOnlyList<Notification> unread = await notifications.ListUnreadForUpdateAsync(recipientId, ct);
                foreach (Notification notification in unread)
                {
                    notification.MarkRead(clock);
                }

                return true;
            },
            cancellationToken);
}
