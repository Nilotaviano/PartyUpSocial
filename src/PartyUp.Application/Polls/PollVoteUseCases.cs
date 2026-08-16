using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Application.Polls;

/// <summary>
/// The registered voter's own surface over their participation: casting a vote, and D-186's
/// self-removal, which withdraws those same votes. Both resolve through
/// <see cref="PollActorVisibilityGate"/> and share this class's dependency set rather than
/// duplicating it — the leave is the vote surface's own undo, not an organizer command.
/// <para>
/// Vote (PHASE6_DESIGN.md §2 "Vote/revise"): guard <c>now &lt; Deadline</c>
/// (D-079), upsert per (option, participant), first submission sets FirstResponseAt (§12-13,
/// aggregate-enforced write-once). Guests vote through <see cref="GuestPollUseCases"/> instead.
/// Resolves through <see cref="PollActorVisibilityGate"/> before any domain call -- a stranger or
/// blocked actor gets the same <c>poll_not_found</c> a missing poll gives, never a state-revealing
/// <c>poll_voting_closed</c>.
/// </para>
/// </summary>
public sealed class PollVoteUseCases(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    IFriendGroupRepository groups,
    IPollNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<PollCommandResult> VoteAsync(
        PollId pollId,
        UserId actorId,
        PollOptionId optionId,
        VoteChoice choice,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                PollForUpdate? forUpdate = await PollActorVisibilityGate.LoadVisibleForUpdateAsync(
                    pollId, actorId, polls, pollLocks, friendships, blocks, groups, ct);
                if (forUpdate is null)
                {
                    return PollCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;

                try
                {
                    poll.Vote(ParticipantRef.ForUser(actorId), optionId, choice, clock);
                }
                catch (DomainException exception)
                {
                    return PollCommandResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return PollCommandResult.Ok(poll);
            },
            cancellationToken);

    /// <summary>
    /// D-186: the caller removes themselves. The gate runs first, so a caller who cannot see the
    /// poll gets the same <c>poll_not_found</c> a missing poll gives; a visible poll the caller is
    /// not on gets it too, since "you are not a participant" and "there is no such poll" must stay
    /// indistinguishable. Silent by construction — <see cref="Poll.Leave"/> raises no event, so the
    /// materializer has nothing to write for anyone (D-088), and the deleted rows are handed to the
    /// repository inside the same transaction as the aggregate mutation (A-016).
    /// </summary>
    public Task<PollCommandResult> LeavePollAsync(
        PollId pollId,
        UserId actorId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                PollForUpdate? forUpdate = await PollActorVisibilityGate.LoadVisibleForUpdateAsync(
                    pollId, actorId, polls, pollLocks, friendships, blocks, groups, ct);
                if (forUpdate is null)
                {
                    return PollCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                ParticipantRef participant = ParticipantRef.ForUser(actorId);

                // Captured before the domain mutation severs them from the aggregate's collections
                // (see IPollRepository.RemoveParticipationAndWithdrawnVotes for why the deletion
                // must be told to EF explicitly rather than inferred from collection severance).
                PollParticipant? row = poll.FindParticipant(participant);
                if (row is null)
                {
                    return PollCommandResult.Failed("poll_not_found");
                }

                List<PollVote> withdrawnVotes = [.. poll.VotesOf(participant)];

                try
                {
                    poll.Leave(actorId, clock);
                }
                catch (DomainException exception)
                {
                    return PollCommandResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                polls.RemoveParticipationAndWithdrawnVotes(row, withdrawnVotes);

                // Leaving is silent and raises nothing, but a due transition ApplyDueTransitions
                // applied inline (e.g. a late Close) still needs to materialize.
                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return PollCommandResult.Ok(poll);
            },
            cancellationToken);
}
