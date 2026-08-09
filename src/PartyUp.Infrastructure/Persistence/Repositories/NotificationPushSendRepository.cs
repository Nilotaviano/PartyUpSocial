using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class NotificationPushSendRepository(AppDbContext context) : INotificationPushSendRepository
{
    public async Task<Guid?> ClaimAsync(
        NotificationId notificationId,
        PushSubscriptionId pushSubscriptionId,
        IClock clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);

        List<Guid> claimed = await context.Database.SqlQuery<Guid>($"""
            INSERT INTO notification_push_sends (id, notification_id, push_subscription_id, claimed_at, outcome)
            VALUES ({Guid.CreateVersion7()}, {notificationId.Value}, {pushSubscriptionId.Value}, {clock.GetCurrentInstant()}, 0)
            ON CONFLICT (notification_id, push_subscription_id) DO NOTHING
            RETURNING id
            """).ToListAsync(cancellationToken);

        return claimed.Count > 0 ? claimed[0] : null;
    }
}
