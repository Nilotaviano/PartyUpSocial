using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Notifications;

/// <summary>
/// The send-log row (PHASE7_DESIGN.md §1). No Domain aggregate owns it: it exists purely to make
/// <c>ux_notification_push_sends_once (notification_id, push_subscription_id)</c> the whole dispatch
/// idempotency mechanism (ARCHITECTURE §3), reached only through
/// <c>PartyUp.Infrastructure.Persistence.Repositories.NotificationPushSendRepository.ClaimAsync</c>'s
/// <c>INSERT ... ON CONFLICT DO NOTHING RETURNING id</c>. No payload column, ever; no <c>attempts</c>
/// column either — nothing retries a claimed send (§3's eviction rule).
/// </summary>
public sealed class NotificationPushSend
{
    private NotificationPushSend()
    {
    }

    public Guid Id { get; private set; }

    public NotificationId NotificationId { get; private set; }

    public PushSubscriptionId PushSubscriptionId { get; private set; }

    public Instant ClaimedAt { get; private set; }

    public PushSendOutcome Outcome { get; private set; }
}

public enum PushSendOutcome
{
    Claimed = 0,
    Delivered = 1,
    Gone = 2,
    Failed = 3,
}
