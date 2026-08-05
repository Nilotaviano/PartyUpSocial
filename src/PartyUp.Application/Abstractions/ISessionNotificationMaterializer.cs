using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// Turns drained Session domain events into notification rows under the §6.3 recipient contract
/// (mute suppression, access-scoped cancellation, no block disclosure). Declared in S1 alongside the
/// other Application ports; implemented and registered by S2 (PHASE4_DESIGN.md §5.1, §6.3).
/// </summary>
public interface ISessionNotificationMaterializer
{
    Task MaterializeAsync(
        Session session,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken);

    /// <summary>
    /// The deletion executor's hook (DOMAIN_MODEL.md §11 step 5, PHASE4_DESIGN.md §6.3): excludes
    /// <paramref name="excludeUserId"/> from every recipient this batch of events would otherwise
    /// compute — the user whose own deletion drained them never receives a notification about it. The
    /// default forwards to the unfiltered overload for a materializer that has no exclusion to apply;
    /// the real adapter overrides this to thread <paramref name="excludeUserId"/> into
    /// <c>RecipientFilter</c>.
    /// </summary>
    Task MaterializeAsync(
        Session session,
        IReadOnlyList<IDomainEvent> events,
        UserId? excludeUserId,
        CancellationToken cancellationToken) =>
        MaterializeAsync(session, events, cancellationToken);
}
