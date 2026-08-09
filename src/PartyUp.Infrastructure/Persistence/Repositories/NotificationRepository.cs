using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class NotificationRepository(AppDbContext context) : INotificationRepository
{
    public void Add(Notification notification) => context.Notifications.Add(notification);

    public Task<Notification?> FindAsync(NotificationId id, CancellationToken cancellationToken) =>
        context.Notifications.AsNoTracking().FirstOrDefaultAsync(notification => notification.Id == id, cancellationToken);

    public async Task AddSteamMatchIfAbsentAsync(
        Notification notification,
        CancellationToken cancellationToken)
    {
        if (notification.Type != NotificationType.SteamImportMatch ||
            notification.ActorUserId is not { } actorUserId)
        {
            throw new ArgumentException(
                "Only a SteamImportMatch notification can use first-observation insertion.",
                nameof(notification));
        }

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications
                (id, recipient_user_id, type, actor_user_id, created_at)
            VALUES
                ({notification.Id.Value},
                 {notification.RecipientUserId.Value},
                 {(short)notification.Type},
                 {actorUserId.Value},
                 {notification.CreatedAt})
            ON CONFLICT (recipient_user_id, actor_user_id) WHERE type = 24
            DO NOTHING
            """, cancellationToken);
    }

    public async Task DeleteForRecipientAsync(
        UserId recipientUserId,
        CancellationToken cancellationToken) =>
        await context.Notifications
            .Where(notification => notification.RecipientUserId == recipientUserId)
            .ExecuteDeleteAsync(cancellationToken);
}
