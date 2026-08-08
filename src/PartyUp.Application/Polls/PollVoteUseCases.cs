using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Application.Polls;

/// <summary>
/// The registered voter surface (PHASE6_DESIGN.md §2 "Vote/revise"): guard <c>now &lt; Deadline</c>
/// (D-079), upsert per (option, participant), first submission sets FirstResponseAt (§12-13,
/// aggregate-enforced write-once). Guests vote through <see cref="GuestPollUseCases"/> instead.
/// Resolves through <see cref="PollActorVisibilityGate"/> before any domain call -- a stranger or
/// blocked actor gets the same <c>poll_not_found</c> a missing poll gives, never a state-revealing
/// <c>poll_voting_closed</c>.
/// </summary>
public sealed class PollVoteUseCases(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
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
                    pollId, actorId, polls, pollLocks, friendships, blocks, ct);
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
}
