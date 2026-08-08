using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Polls;

/// <summary>
/// The fail-closed visibility gate shared by every actor-only poll write that does not introduce a
/// new counterpart onto the poll (vote/suggest/withdraw): mirrors
/// <see cref="PollJoinRequestUseCases.RequestJoinAsync"/>'s probe-&gt;lock-&gt;re-resolve shape, minus
/// its "new participant" step, so a stranger or blocked actor is turned away with the same
/// <c>poll_not_found</c> a missing poll gives -- BEFORE any domain call whose own guard order
/// (deadline before participation, <see cref="Poll.RequireVotingOpen"/>) would otherwise leak the
/// poll's existence and state to a viewer with zero access.
/// </summary>
internal static class PollActorVisibilityGate
{
    public static async Task<PollForUpdate?> LoadVisibleForUpdateAsync(
        PollId pollId,
        UserId actorId,
        IPollRepository polls,
        IPollLockRepository pollLocks,
        IFriendshipRepository friendships,
        IBlockRepository blocks,
        CancellationToken cancellationToken)
    {
        // Step 1: resolve without locking arbitrary ids -- a genuinely missing poll costs nothing.
        Poll? probe = await polls.FindNoTrackingAsync(pollId, cancellationToken);
        if (probe is null)
        {
            return null;
        }

        // Step 2: lock the actor row (already known, no new counterpart to add here).
        await pollLocks.LockUsersAsync([actorId], cancellationToken);

        // Step 3: lock friendship/block pairs against the probed roster, one statement per table.
        HashSet<UserId> probedCounterparts = CounterpartsOf(probe, actorId);
        IReadOnlyList<Friendship> probedFriendships = probedCounterparts.Count == 0
            ? []
            : await friendships.LockPairsAsync(actorId, probedCounterparts, cancellationToken);
        IReadOnlyList<Block> probedBlocks = probedCounterparts.Count == 0
            ? []
            : await blocks.LockPairsAsync(actorId, probedCounterparts, cancellationToken);

        // Step 4: lock the poll parent, then load its authoritative children.
        await pollLocks.LockPollsAsync([pollId], cancellationToken);
        PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, cancellationToken);
        if (forUpdate is null)
        {
            return null;
        }

        Poll poll = forUpdate.Poll;

        // Step 5: re-lock any counterpart the fresh roster reveals that the probe missed, then
        // re-evaluate visibility under the fresh locks.
        HashSet<UserId> freshCounterparts = CounterpartsOf(poll, actorId);
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

        return VisibilityEngine.ResolvePoll(poll, viewer).IsVisible ? forUpdate : null;
    }

    private static HashSet<UserId> CounterpartsOf(Poll poll, UserId actorId)
    {
        HashSet<UserId> counterparts = [poll.OrganizerId];
        foreach (PollParticipant participant in poll.Participants)
        {
            if (participant.UserId is { } userId)
            {
                counterparts.Add(userId);
            }
        }

        counterparts.Remove(actorId);
        return counterparts;
    }
}
