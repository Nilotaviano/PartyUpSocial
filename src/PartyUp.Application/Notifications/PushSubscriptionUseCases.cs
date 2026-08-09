using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Push;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Notifications;

public sealed record CreatePushSubscriptionResult(bool Succeeded, string? Code, Guid? SubscriptionId)
{
    public static CreatePushSubscriptionResult Ok(Guid id) => new(true, null, id);

    public static CreatePushSubscriptionResult Failed(string code) => new(false, code, null);
}

/// <summary>
/// PHASE7_DESIGN.md §4's <c>CreatePushSubscription</c>: channel validation, the §8 configuration
/// gate, then the S1-carried rotating upsert (<see cref="IPushSubscriptionRepository.UpsertActiveAsync"/>).
/// Losing the client-side id is recoverable without a second route — re-POSTing the same endpoint
/// returns the existing subscription's id.
/// </summary>
public sealed class CreatePushSubscriptionUseCase(
    IPushSubscriptionRepository subscriptions,
    IPushSender sender,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<CreatePushSubscriptionResult> ExecuteAsync(
        UserId userId,
        string channel,
        string endpoint,
        string publicKey,
        string authSecret,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(channel, "webpush", StringComparison.Ordinal))
        {
            return Task.FromResult(CreatePushSubscriptionResult.Failed("push_channel_unsupported"));
        }

        if (!sender.IsConfigured)
        {
            return Task.FromResult(CreatePushSubscriptionResult.Failed("push_not_configured"));
        }

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                PushSubscriptionId id = await subscriptions.UpsertActiveAsync(
                    userId, endpoint, publicKey, authSecret, clock, ct);
                return CreatePushSubscriptionResult.Ok(id.Value);
            },
            cancellationToken);
    }
}

/// <summary>
/// PHASE7_DESIGN.md §4's <c>DeletePushSubscription</c>: idempotent 204 either way; an unknown id or
/// one belonging to another user is the identical <c>404 push_subscription_not_found</c> (fail closed
/// — <see cref="IPushSubscriptionRepository.FindOwnedAsync"/> already scopes to the caller).
/// </summary>
public sealed class DeletePushSubscriptionUseCase(
    IPushSubscriptionRepository subscriptions,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<bool> ExecuteAsync(
        PushSubscriptionId id,
        UserId userId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                PushSubscription? subscription = await subscriptions.FindOwnedAsync(id, userId, ct);
                if (subscription is null)
                {
                    return false;
                }

                subscription.Revoke(clock);
                return true;
            },
            cancellationToken);
}
