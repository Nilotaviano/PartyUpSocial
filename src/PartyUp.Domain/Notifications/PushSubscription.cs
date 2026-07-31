using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Notifications;

/// <summary>
/// One Web Push endpoint a user has registered (DOMAIN_MODEL §2 — "shape only in Phase 1; transport
/// in Phase 7"). The domain owns the row's identity, ownership and lifecycle; encryption, VAPID and
/// delivery, including what a gone endpoint means, belong to Phase 7 (A-013).
/// </summary>
public sealed class PushSubscription
{
    public const int MaximumEndpointLength = 2048;

    public const int MaximumKeyLength = 256;

    private PushSubscription()
    {
        Endpoint = string.Empty;
        PublicKey = string.Empty;
        AuthSecret = string.Empty;
    }

    private PushSubscription(
        PushSubscriptionId id,
        UserId userId,
        string endpoint,
        string publicKey,
        string authSecret,
        Instant createdAt)
    {
        Id = id;
        UserId = userId;
        Endpoint = endpoint;
        PublicKey = publicKey;
        AuthSecret = authSecret;
        CreatedAt = createdAt;
    }

    public PushSubscriptionId Id { get; private set; }

    public UserId UserId { get; private set; }

    /// <summary>The push service URL Phase 7 posts to; logically unique per user.</summary>
    public string Endpoint { get; private set; }

    /// <summary>The subscription's P-256 ECDH public key ("p256dh").</summary>
    public string PublicKey { get; private set; }

    /// <summary>The subscription's auth secret — a credential, and never logged (A-015).</summary>
    public string AuthSecret { get; private set; }

    public Instant CreatedAt { get; private set; }

    public Instant? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    public static PushSubscription Create(
        UserId userId,
        string endpoint,
        string publicKey,
        string authSecret,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new PushSubscription(
            PushSubscriptionId.New(),
            userId,
            RequireText(endpoint, MaximumEndpointLength, "endpoint"),
            RequireText(publicKey, MaximumKeyLength, "public key"),
            RequireText(authSecret, MaximumKeyLength, "auth secret"),
            clock.GetCurrentInstant());
    }

    /// <summary>Idempotent: unsubscribing twice keeps the first instant.</summary>
    public void Revoke(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RevokedAt ??= clock.GetCurrentInstant();
    }

    private static string RequireText(string value, int maximumLength, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"A push subscription requires an {what}."));
        }

        string trimmed = value.Trim();

        if (trimmed.Length > maximumLength)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A push subscription {what} is at most {maximumLength} characters, but was {trimmed.Length}."));
        }

        return trimmed;
    }
}
