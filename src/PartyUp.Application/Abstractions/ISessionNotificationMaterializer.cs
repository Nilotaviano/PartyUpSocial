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
}
