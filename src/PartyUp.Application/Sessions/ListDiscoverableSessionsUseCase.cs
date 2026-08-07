using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Sessions;

/// <summary>
/// PHASE5_DESIGN.md §6.3/A-077 (c): the <c>/discover</c> surface — a thin read wrapper over
/// <see cref="ISessionReadRepository.ListDiscoverAsync"/>, which is where A-077 (c)'s exclusion set
/// and the <c>inheritedOnly</c> fragment are applied. No masking beyond that: rule 4's block filter is
/// already inside the fragment, so a session ever reaching this list never has a blocked organizer.
/// </summary>
public sealed class ListDiscoverableSessionsUseCase(ISessionReadRepository reads)
{
    public Task<SessionPage<SessionSummary>> ExecuteAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        reads.ListDiscoverAsync(viewerId, cursor, pageSize, cancellationToken);
}
