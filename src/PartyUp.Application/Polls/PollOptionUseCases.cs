using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Application.Polls;

public sealed record PollCommandResult(bool Succeeded, string? Code, Poll? Poll)
{
    public static PollCommandResult Ok(Poll poll) => new(true, null, poll);

    public static PollCommandResult Failed(string code) => new(false, code, null);
}

public sealed record SuggestOptionResult(bool Succeeded, string? Code, Poll? Poll, PollOptionId? OptionId)
{
    public static SuggestOptionResult Ok(Poll poll, PollOptionId optionId) => new(true, null, poll, optionId);

    public static SuggestOptionResult Failed(string code) => new(false, code, null, null);
}

/// <summary>
/// Registered-actor option commands (PHASE6_DESIGN.md §2): SuggestOption for any Active voter
/// (D-033), DeleteOption for the organizer only (D-080/D-088). Guests suggest through
/// <see cref="GuestPollUseCases"/> instead — same domain entry point, different actor resolution.
/// </summary>
public sealed class PollOptionUseCases(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    IFriendGroupRepository groups,
    IPollNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    /// <summary>
    /// Resolves through <see cref="PollActorVisibilityGate"/> before any domain call -- a stranger or
    /// blocked actor gets the same <c>poll_not_found</c> a missing poll gives, never a
    /// state-revealing <c>poll_voting_closed</c>. D-211: the suggested option carries its own
    /// length; an absent one takes the same D-129 default direct session creation takes.
    /// </summary>
    public Task<SuggestOptionResult> SuggestOptionAsync(
        PollId pollId,
        UserId actorId,
        Instant startTime,
        int? durationMinutes,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                PollForUpdate? forUpdate = await PollActorVisibilityGate.LoadVisibleForUpdateAsync(
                    pollId, actorId, polls, pollLocks, friendships, blocks, groups, ct);
                if (forUpdate is null)
                {
                    return SuggestOptionResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                PollOption option;
                try
                {
                    option = poll.SuggestOption(
                        ParticipantRef.ForUser(actorId),
                        startTime,
                        Duration15.FromMinutes(durationMinutes ?? CreateSessionUseCase.DefaultDurationMinutes),
                        clock);
                }
                catch (DomainException exception)
                {
                    return SuggestOptionResult.Failed(CodeFor(exception));
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return SuggestOptionResult.Ok(poll, option.Id);
            },
            cancellationToken);

    /// <summary>D-080/D-088: organizer-only, silent, votes cascade — FirstResponseAt never recomputed (D-172).</summary>
    public Task<PollCommandResult> DeleteOptionAsync(
        PollId pollId,
        UserId organizerId,
        PollOptionId optionId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await pollLocks.LockUsersAsync([organizerId], ct);
                await pollLocks.LockPollsAsync([pollId], ct);

                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return PollCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                if (poll.OrganizerId != organizerId)
                {
                    return PollCommandResult.Failed("poll_not_found");
                }

                // Captured before the domain mutation removes them from the aggregate's own
                // collections (see IPollRepository.RemoveOptionAndCascadedVotes for why the removal
                // must be told to EF explicitly rather than inferred from collection severance).
                PollOption? optionBeingDeleted = poll.Options.FirstOrDefault(o => o.Id == optionId);
                List<PollVote> cascadedVotes = [.. poll.Votes.Where(v => v.OptionId == optionId)];

                try
                {
                    poll.DeleteOption(optionId, clock);
                }
                catch (DomainException exception)
                {
                    return PollCommandResult.Failed(CodeFor(exception));
                }

                if (optionBeingDeleted is not null)
                {
                    polls.RemoveOptionAndCascadedVotes(optionBeingDeleted, cascadedVotes);
                }

                // DeleteOption is silent (D-088) and raises no domain event, but any due transition
                // ApplyDueTransitions applied inline (e.g. a late Close) still needs to materialize.
                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return PollCommandResult.Ok(poll);
            },
            cancellationToken);

    internal static string CodeFor(DomainException exception) => exception.Error switch
    {
        DomainError.NotFound => "poll_option_not_found",
        DomainError.LimitExceeded => "poll_option_cap",
        DomainError.WindowClosed => "poll_voting_closed",
        DomainError.TerminalState => "poll_terminal",
        DomainError.InvalidTransition => "poll_invalid_transition",
        DomainError.RemovalIsPermanent => "poll_participant_removed",
        DomainError.Conflict => "poll_conflict",
        _ => "poll_validation",
    };
}
