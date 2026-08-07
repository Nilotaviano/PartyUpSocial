using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Sessions;

/// <summary>
/// PHASE5_DESIGN.md §5.1/P5-HOME-01: D-103's Home region 1. A thin read wrapper over
/// <see cref="ISessionReadRepository.ListActionableAsync"/>, which applies the A-077 fragment, the
/// future/Unconfirmed-or-Confirmed scope, and the 50-per-list cap with <c>hasMore</c>.
/// </summary>
public sealed class GetActionableInboxUseCase(ISessionReadRepository reads, IClock clock)
{
    public const int Cap = 50;

    public Task<ActionableInbox> ExecuteAsync(UserId viewerId, CancellationToken cancellationToken) =>
        reads.ListActionableAsync(viewerId, clock.GetCurrentInstant(), Cap, cancellationToken);
}
