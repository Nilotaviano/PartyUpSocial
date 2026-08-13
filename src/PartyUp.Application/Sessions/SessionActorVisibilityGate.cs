using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Sessions;

/// <summary>
/// The fail-closed visibility gate shared by every actor-only session write that does not introduce a
/// new counterpart onto the session (RSVP, decline, reminder choice): mirrors
/// <see cref="PartyUp.Application.Polls.PollActorVisibilityGate"/> and
/// <see cref="SessionJoinRequestUseCases.RequestJoinAsync"/>'s probe-&gt;lock-&gt;re-resolve shape, minus
/// its "new participant" step, so a stranger or blocked actor is turned away with the same
/// <c>session_not_found</c> a missing session gives (A-055) -- BEFORE any domain call whose own guard
/// order (roster mutability before participation) would otherwise leak the session's existence and
/// state to a viewer with zero access.
/// </summary>
internal static class SessionActorVisibilityGate
{
    public static async Task<SessionForUpdate?> LoadVisibleForUpdateAsync(
        SessionId sessionId,
        UserId actorId,
        ISessionRepository sessions,
        ISessionLockRepository sessionLocks,
        IFriendshipRepository friendships,
        IBlockRepository blocks,
        CancellationToken cancellationToken)
    {
        // Step 1: resolve without locking arbitrary ids -- a genuinely missing session costs nothing.
        Session? probe = await sessions.FindNoTrackingAsync(sessionId, cancellationToken);
        if (probe is null)
        {
            return null;
        }

        // Step 2: lock the actor row (already known, no new counterpart to add here).
        await sessionLocks.LockUsersAsync([actorId], cancellationToken);

        // Step 3: lock friendship/block pairs against the probed roster, one statement per table.
        HashSet<UserId> probedCounterparts = CounterpartsOf(probe, actorId);
        IReadOnlyList<Friendship> probedFriendships = probedCounterparts.Count == 0
            ? []
            : await friendships.LockPairsAsync(actorId, probedCounterparts, cancellationToken);
        IReadOnlyList<Block> probedBlocks = probedCounterparts.Count == 0
            ? []
            : await blocks.LockPairsAsync(actorId, probedCounterparts, cancellationToken);

        // Step 4: lock the session parent, then load its authoritative children.
        await sessionLocks.LockSessionsAsync([sessionId], cancellationToken);
        SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(sessionId, cancellationToken);
        if (forUpdate is null)
        {
            return null;
        }

        Session session = forUpdate.Session;

        // Step 5: re-lock any counterpart the fresh roster reveals that the probe missed, then
        // re-evaluate visibility under the fresh locks.
        HashSet<UserId> freshCounterparts = CounterpartsOf(session, actorId);
        freshCounterparts.ExceptWith(probedCounterparts);

        IReadOnlyList<Friendship> extraFriendships = freshCounterparts.Count == 0
            ? []
            : await friendships.LockPairsAsync(actorId, freshCounterparts, cancellationToken);
        IReadOnlyList<Block> extraBlocks = freshCounterparts.Count == 0
            ? []
            : await blocks.LockPairsAsync(actorId, freshCounterparts, cancellationToken);

        Viewer viewer = Viewer.From(
            actorId,
            [.. probedFriendships, .. extraFriendships],
            [.. probedBlocks, .. extraBlocks]);

        return VisibilityEngine.ResolveSession(session, viewer).IsVisible ? forUpdate : null;
    }

    private static HashSet<UserId> CounterpartsOf(Session session, UserId actorId)
    {
        HashSet<UserId> counterparts = [session.OrganizerId];
        foreach (Participation participation in session.Participations)
        {
            if (participation.UserId is { } userId)
            {
                counterparts.Add(userId);
            }
        }

        counterparts.Remove(actorId);
        return counterparts;
    }
}
