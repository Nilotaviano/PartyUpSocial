using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Notifications;

/// <summary>
/// PHASE7_DESIGN.md §4's <c>ListNotifications</c>/<c>GetUnreadNotificationCount</c>: pages
/// <see cref="INotificationReadRepository"/>'s A-072 keyset, then projects every row through the
/// shipped S2 <see cref="NotificationReadUseCases"/> — the only legal row projector (masking,
/// subject-access re-resolution, recipient-locale rendering).
/// </summary>
public sealed class ListNotificationsUseCase(
    INotificationReadRepository reads,
    NotificationReadUseCases projector)
{
    public async Task<SessionPage<NotificationReadModel>> ExecuteAsync(
        UserId recipientId,
        bool unreadOnly,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        SessionListScope scope = unreadOnly
            ? SessionListScope.NotificationsUnread
            : SessionListScope.NotificationsAll;

        SessionPage<Notification> page = await reads.ListAsync(
            recipientId, unreadOnly, cursor, pageSize, scope, cancellationToken);
        IReadOnlyList<NotificationReadModel> rows = await projector.ProjectPageAsync(
            page.Items, recipientId, cancellationToken);

        return new SessionPage<NotificationReadModel>(rows, page.NextCursor);
    }

    public Task<int> UnreadCountAsync(UserId recipientId, CancellationToken cancellationToken) =>
        reads.CountUnreadAsync(recipientId, cancellationToken);
}
