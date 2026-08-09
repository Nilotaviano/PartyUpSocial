using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

public interface INotificationPushSendRepository
{
    /// <summary>
    /// The whole dispatch idempotency mechanism (ARCHITECTURE §3, PHASE7_DESIGN.md §1): an
    /// <c>INSERT ... ON CONFLICT DO NOTHING RETURNING id</c> against
    /// <c>ux_notification_push_sends_once (notification_id, push_subscription_id)</c> — never an
    /// app-level pre-check. A claim loser writes nothing and returns <see langword="null"/>.
    /// </summary>
    Task<Guid?> ClaimAsync(
        NotificationId notificationId,
        PushSubscriptionId pushSubscriptionId,
        IClock clock,
        CancellationToken cancellationToken);
}
