using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// PHASE7_DESIGN.md §4's notification-list read repository, mirroring <c>PollReadRepository</c>'s
/// id-then-hydrate idiom: a raw keyset SQL query resolves the page's ids in order, then a plain EF
/// query hydrates the aggregates (no visibility predicate to splice in here, unlike the poll/session
/// list reads — masking and subject re-resolution are S2's job, downstream of this repository).
/// </summary>
public sealed class NotificationReadRepository(AppDbContext context) : INotificationReadRepository
{
    public async Task<SessionPage<Notification>> ListAsync(
        UserId recipientId,
        bool unreadOnly,
        SessionListCursor? cursor,
        int pageSize,
        SessionListScope scope,
        CancellationToken cancellationToken)
    {
        Guid recipient = recipientId.Value;
        string unreadPredicate = unreadOnly ? "AND read_at IS NULL" : string.Empty;

        List<Guid> ids = cursor is { } after
            ? await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM notifications
                WHERE recipient_user_id = {0}
                  {{unreadPredicate}}
                  AND (created_at, id) < ({1}, {2})
                ORDER BY created_at DESC, id DESC
                LIMIT {3}
                """, recipient, after.SortKey, after.Id, pageSize))
                .ToListAsync(cancellationToken)
            : await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM notifications
                WHERE recipient_user_id = {0}
                  {{unreadPredicate}}
                ORDER BY created_at DESC, id DESC
                LIMIT {1}
                """, recipient, pageSize))
                .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return new SessionPage<Notification>([], null);
        }

        List<NotificationId> notificationIds = [.. ids.Select(NotificationId.From)];
        List<Notification> rows = await context.Notifications
            .AsNoTracking()
            .Where(notification => notificationIds.Contains(notification.Id))
            .ToListAsync(cancellationToken);
        Dictionary<Guid, Notification> byId = rows.ToDictionary(notification => notification.Id.Value);
        List<Notification> ordered = [.. ids.Select(id => byId[id])];

        string? nextCursor = ordered.Count == pageSize
            ? new SessionListCursor(ordered[^1].CreatedAt, ordered[^1].Id.Value, scope).Encode()
            : null;

        return new SessionPage<Notification>(ordered, nextCursor);
    }

    public Task<int> CountUnreadAsync(UserId recipientId, CancellationToken cancellationToken) =>
        context.Notifications
            .AsNoTracking()
            .CountAsync(
                notification => notification.RecipientUserId == recipientId && notification.ReadAt == null,
                cancellationToken);
}
