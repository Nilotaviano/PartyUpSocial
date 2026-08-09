using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Push;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Infrastructure.Persistence.Repositories;

// The push channel is fixed at WebPush(0) by construction (PHASE7_DESIGN.md §1): PushSubscription
// carries no Channel member -- only WebPush exists today -- so this adapter writes the shadow "Channel"
// column as a constant; a native channel adds its own write path rather than widening this one.
public sealed class PushSubscriptionRepository(AppDbContext context) : IPushSubscriptionRepository
{
    private const short WebPushChannel = 0;

    public async Task<PushSubscriptionId> UpsertActiveAsync(
        UserId userId,
        string endpoint,
        string publicKey,
        string authSecret,
        IClock clock,
        CancellationToken cancellationToken)
    {
        string endpointHash = HashEndpoint(endpoint);

        PushSubscription? existing = await context.PushSubscriptions
            .FirstOrDefaultAsync(
                subscription => subscription.UserId == userId &&
                    subscription.RevokedAt == null &&
                    EF.Property<short>(subscription, "Channel") == WebPushChannel &&
                    EF.Property<string>(subscription, "EndpointHash") == endpointHash,
                cancellationToken);

        if (existing is not null)
        {
            return existing.Id;
        }

        PushSubscription subscription = PushSubscription.Create(userId, endpoint, publicKey, authSecret, clock);
        context.PushSubscriptions.Add(subscription);
        context.Entry(subscription).Property("Channel").CurrentValue = WebPushChannel;
        context.Entry(subscription).Property("EndpointHash").CurrentValue = endpointHash;
        return subscription.Id;
    }

    public Task<PushSubscription?> FindOwnedAsync(
        PushSubscriptionId id,
        UserId userId,
        CancellationToken cancellationToken) =>
        context.PushSubscriptions.SingleOrDefaultAsync(
            subscription => subscription.Id == id && subscription.UserId == userId,
            cancellationToken);

    public Task DeleteForUserAsync(UserId userId, CancellationToken cancellationToken) =>
        context.PushSubscriptions
            .Where(subscription => subscription.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

    // S3's fan-out source and the S1 verification carry: user_id = recipient AND revoked_at IS NULL
    // only -- a notification can never claim against another user's subscription.
    public async Task<IReadOnlyList<PushSubscriptionTarget>> ListActiveForUserAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        await context.PushSubscriptions
            .Where(subscription => subscription.UserId == userId && subscription.RevokedAt == null)
            .OrderBy(subscription => subscription.Id)
            .Select(subscription => new PushSubscriptionTarget(
                subscription.Id, subscription.Endpoint, subscription.PublicKey, subscription.AuthSecret))
            .ToListAsync(cancellationToken);

    public async Task RevokeAsync(PushSubscriptionId id, IClock clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);

        PushSubscription? subscription = await context.PushSubscriptions
            .FirstOrDefaultAsync(subscription => subscription.Id == id, cancellationToken);
        if (subscription is null)
        {
            return;
        }

        subscription.Revoke(clock);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string HashEndpoint(string endpoint) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
}
