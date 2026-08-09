using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Push;
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

    public async Task UpdateOutcomeAsync(
        Guid sendLogId,
        PushSendOutcome outcome,
        CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notification_push_sends SET outcome = {(short)outcome}
            WHERE id = {sendLogId}
            """, cancellationToken);
}
