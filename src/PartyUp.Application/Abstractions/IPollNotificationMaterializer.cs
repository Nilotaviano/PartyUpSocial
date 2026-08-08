using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// Turns drained Poll domain events into notification rows under the §10 poll recipient contract
/// (mute suppression, no guest disclosure — D-167), mirroring
/// <see cref="ISessionNotificationMaterializer"/>. <see cref="RecipientComputer.ForPollEvent"/> is
/// the pure recipient contract this adapter routes every event through.
/// </summary>
public interface IPollNotificationMaterializer
{
    Task MaterializeAsync(
        Poll poll,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken);

    /// <summary>
    /// The deletion executor's hook (DOMAIN_MODEL.md §11 step 5), mirroring
    /// <see cref="ISessionNotificationMaterializer"/>'s overload: excludes
    /// <paramref name="excludeUserId"/> from every recipient this batch of events would otherwise
    /// compute. The default forwards to the unfiltered overload; the real adapter overrides this to
    /// thread <paramref name="excludeUserId"/> into <c>RecipientFilter</c>.
    /// </summary>
    Task MaterializeAsync(
        Poll poll,
        IReadOnlyList<IDomainEvent> events,
        UserId? excludeUserId,
        CancellationToken cancellationToken) =>
        MaterializeAsync(poll, events, cancellationToken);
}
