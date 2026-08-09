using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// PHASE7_DESIGN.md §1's push-subscription store. <c>PushSubscription</c> ships shape-only with no
/// <c>Channel</c> member (only WebPush exists today), so the channel and the endpoint hash are written
/// as adapter-owned shadow properties — never surfaced through this port.
/// </summary>
public interface IPushSubscriptionRepository
{
    /// <summary>
    /// Idempotent upsert on (user, channel, endpoint hash): an existing active row for the same
    /// endpoint is returned unchanged; otherwise a new subscription is created. Returns the row's id
    /// either way.
    /// </summary>
    Task<PushSubscriptionId> UpsertActiveAsync(
        UserId userId,
        string endpoint,
        string publicKey,
        string authSecret,
        IClock clock,
        CancellationToken cancellationToken);

    Task<PushSubscription?> FindOwnedAsync(
        PushSubscriptionId id,
        UserId userId,
        CancellationToken cancellationToken);

    /// <summary>§11 step 5: every subscription row for the user, active or already revoked.</summary>
    Task DeleteForUserAsync(UserId userId, CancellationToken cancellationToken);
}
