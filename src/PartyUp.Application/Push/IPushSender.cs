namespace PartyUp.Application.Push;

/// <summary>
/// The channel-scoped transport seam (PHASE7_DESIGN.md §3): one adapter per <c>push_subscriptions.channel</c>
/// value, selected by <c>Push:Provider</c>. A native channel adds a sibling adapter rather than widening
/// this one — WebPush stays the only channel this interface's shipped implementations speak.
/// </summary>
public interface IPushSender
{
    /// <summary>
    /// Whether this sender can actually deliver right now (§8: absent/partial VAPID configuration).
    /// The dispatch job must no-op cleanly — touching no row — rather than call
    /// <see cref="SendAsync"/> when this is <see langword="false"/>.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends one payload to one subscription. Never throws for an ordinary push-service rejection
    /// (404/410/429/4xx/5xx) — those resolve to a <see cref="PushSendResult"/> outcome; only a caller
    /// bug (null arguments) or a truly unexpected transport failure should propagate.
    /// </summary>
    Task<PushSendResult> SendAsync(
        PushSubscriptionTarget target,
        PushPayload payload,
        CancellationToken cancellationToken);
}
