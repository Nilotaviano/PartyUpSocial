using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Sessions;

/// <summary>
/// The registered-viewer session reads (PHASE4_DESIGN.md §5.1/§5.2). <c>GetSessionDetail</c> resolves
/// visibility over the single loaded aggregate; the two lists push viewer scoping into
/// <see cref="ISessionReadRepository"/>'s SQL rather than filtering after paging, so a page never
/// shrinks below the requested size because invisible rows were dropped afterward (the S1 carried
/// verifier note).
/// </summary>
public sealed class SessionReadUseCases(
    ISessionRepository sessions,
    ISessionReadRepository reads,
    IFriendshipRepository friendships,
    IBlockRepository blocks)
{
    public async Task<SessionDetail?> GetSessionDetailAsync(
        SessionId sessionId,
        UserId viewerId,
        CancellationToken cancellationToken)
    {
        Session? session = await sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        Viewer viewer = await BuildViewerAsync(viewerId, session, cancellationToken);
        VisibilityResult visibility = VisibilityEngine.ResolveSession(session, viewer);
        if (!visibility.IsVisible)
        {
            return null;
        }

        return await reads.FindDetailAsync(sessionId, cancellationToken);
    }

    public Task<SessionPage<SessionSummary>> ListUpcomingSessionsAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        reads.ListUpcomingAsync(viewerId, cursor, pageSize, cancellationToken);

    public Task<SessionPage<SessionSummary>> ListCompletedHistoryAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        reads.ListHistoryAsync(viewerId, cursor, pageSize, cancellationToken);

    /// <summary>
    /// Builds the minimal §7 viewer facts this one session needs: the organizer plus every other
    /// registered participant, read as two bulk (not per-counterpart) queries — proportionate for a
    /// single-session read, and never a row lock: a GET must take none (F6). The previous
    /// implementation looped <see cref="IBlockRepository.FindPairForUpdateAsync"/> per counterpart,
    /// which takes <c>FOR UPDATE</c> on a read path; this uses the non-locking bulk read instead.
    /// </summary>
    private async Task<Viewer> BuildViewerAsync(UserId viewerId, Session session, CancellationToken cancellationToken)
    {
        HashSet<UserId> counterparts = [session.OrganizerId];
        foreach (Participation participation in session.Participations)
        {
            if (participation.UserId is { } userId && userId != viewerId)
            {
                counterparts.Add(userId);
            }
        }

        counterparts.Remove(viewerId);

        if (counterparts.Count == 0)
        {
            return Viewer.Create(viewerId);
        }

        IReadOnlyList<Friendship> pairFriendships = await friendships.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);
        IReadOnlyList<Block> pairBlocks = await blocks.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);

        List<UserId> friendIds = [.. pairFriendships.Where(f => f.IsAccepted).Select(f => f.Counterpart(viewerId))];
        List<UserId> blockedIds = [.. pairBlocks.Select(b => b.BlockerId == viewerId ? b.BlockedId : b.BlockerId)];

        return Viewer.Create(viewerId, friendIds, blockedIds);
    }
}
