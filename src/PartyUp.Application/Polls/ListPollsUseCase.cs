using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Polls;

/// <summary>
/// PHASE6_DESIGN.md §4.1: <c>GET /polls</c>'s two bands, a thin read wrapper mirroring
/// <c>ListDiscoverableSessionsUseCase</c> -- both bands' filtering and A-072 cursor scoping live in
/// <see cref="IPollReadRepository"/>, over <c>PollVisibilitySql</c>.
/// </summary>
public sealed class ListPollsUseCase(IPollReadRepository reads)
{
    public Task<SessionPage<PollSummary>> ListInvolvedAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        reads.ListInvolvedAsync(viewerId, cursor, pageSize, cancellationToken);

    public Task<SessionPage<PollSummary>> ListDiscoverableAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        reads.ListDiscoverableAsync(viewerId, cursor, pageSize, cancellationToken);
}
