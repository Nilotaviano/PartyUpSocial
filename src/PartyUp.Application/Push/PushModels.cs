using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Push;

/// <summary>
/// The send-log outcome a dispatch attempt resolves to (PHASE7_DESIGN.md §3's eviction rule). Never
/// includes <c>Claimed</c> — that value is written once, by the claim insert itself
/// (<see cref="Abstractions.INotificationPushSendRepository.ClaimAsync"/>), and this port only ever
/// moves a row away from it.
/// </summary>
public enum PushSendOutcome
{
    Delivered = 1,
    Gone = 2,
    Failed = 3,
}

/// <summary>What one send attempt resolved to; <see cref="StatusCode"/> is diagnostic only, never logged with the payload.</summary>
public sealed record PushSendResult(PushSendOutcome Outcome, int? StatusCode);

/// <summary>
/// One live subscription to send to. Carries the credential fields verbatim (§1's A-015 posture:
/// replayed, never hashed) — callers must never log <see cref="Endpoint"/>, <see cref="PublicKey"/> or
/// <see cref="AuthSecret"/>.
/// </summary>
public sealed record PushSubscriptionTarget(PushSubscriptionId Id, string Endpoint, string PublicKey, string AuthSecret);

/// <summary>The whole wire payload (§3.1): built from the recipient's own gated §2 projection, never persisted.</summary>
public sealed record PushPayload(string Title, string Body, string Url, string Tag);
