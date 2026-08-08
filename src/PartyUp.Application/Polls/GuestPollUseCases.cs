using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Polls;

public sealed record GuestPollCommandResult(bool Succeeded, string? Code, Poll? Poll, GuestIdentityId? GuestIdentityId)
{
    public static GuestPollCommandResult Ok(Poll poll, GuestIdentityId? guestIdentityId = null) =>
        new(true, null, poll, guestIdentityId);

    public static GuestPollCommandResult Failed(string code) => new(false, code, null, null);
}

/// <summary>
/// A poll guest bearer's write surface (PHASE6_DESIGN.md §2), mirroring
/// <see cref="GuestSessionUseCases"/>: claim, rename, suggest and vote share the same
/// parent-lock/transaction path as registered mutations — none of them introduces a new registered
/// counterpart, so only the poll parent is locked before occupancy is read. Guests are never
/// notification recipients (D-167). Voting/suggesting before claiming is an explicit 409
/// (<c>guest_link_not_claimed</c>) enforced here — the aggregate's claimed-guest guard
/// (<see cref="Poll"/>'s private <c>RequireClaimedGuest</c>) is not a new domain entry point,
/// matching the ticket's instruction not to add one.
/// </summary>
public sealed class GuestPollUseCases(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IPollNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    /// <summary>D-017: the first visit supplies a display name and becomes a voter (no attendance response — D-098).</summary>
    public Task<GuestPollCommandResult> ClaimGuestLinkAsync(
        PollId pollId,
        GuestLinkId guestLinkId,
        DisplayName displayName,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await pollLocks.LockPollsAsync([pollId], ct);

                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return GuestPollCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                GuestIdentity identity;
                try
                {
                    identity = poll.ClaimGuestLink(guestLinkId, displayName, clock);
                }
                catch (DomainException exception)
                {
                    return GuestPollCommandResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return GuestPollCommandResult.Ok(poll, identity.Id);
            },
            cancellationToken);

    /// <summary>D-044/D-126: renaming through the same link, last write wins — no conflict check.</summary>
    public Task<GuestPollCommandResult> RenameGuestAsync(
        PollId pollId,
        GuestLinkId guestLinkId,
        DisplayName displayName,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await pollLocks.LockPollsAsync([pollId], ct);

                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return GuestPollCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                try
                {
                    poll.RenameGuest(guestLinkId, displayName, clock);
                }
                catch (DomainException exception)
                {
                    return GuestPollCommandResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return GuestPollCommandResult.Ok(poll);
            },
            cancellationToken);

    /// <summary>D-033/D-051: any Active voter suggests, guests included — resolved to the claimed identity behind the link.</summary>
    public Task<GuestPollCommandResult> SuggestOptionAsGuestAsync(
        PollId pollId,
        GuestLinkId guestLinkId,
        Instant startTime,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await pollLocks.LockPollsAsync([pollId], ct);

                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return GuestPollCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                GuestPollCommandResult? claimFailure = RequireClaimedGuest(poll, guestLinkId, out ParticipantRef participant);
                if (claimFailure is not null)
                {
                    return claimFailure;
                }

                try
                {
                    poll.SuggestOption(participant, startTime, clock);
                }
                catch (DomainException exception)
                {
                    return GuestPollCommandResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return GuestPollCommandResult.Ok(poll);
            },
            cancellationToken);

    /// <summary>
    /// D-079/D-126: an unclaimed link voting is a 409 (<c>guest_link_not_claimed</c>) — claiming is
    /// explicit, never implied by a vote call. Last-write-wins across devices once claimed: no
    /// conflict check, the poll parent lock is what makes the write race-free.
    /// </summary>
    public Task<GuestPollCommandResult> VoteAsGuestAsync(
        PollId pollId,
        GuestLinkId guestLinkId,
        PollOptionId optionId,
        VoteChoice choice,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await pollLocks.LockPollsAsync([pollId], ct);

                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return GuestPollCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                GuestPollCommandResult? claimFailure = RequireClaimedGuest(poll, guestLinkId, out ParticipantRef participant);
                if (claimFailure is not null)
                {
                    return claimFailure;
                }

                try
                {
                    poll.Vote(participant, optionId, choice, clock);
                }
                catch (DomainException exception)
                {
                    return GuestPollCommandResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return GuestPollCommandResult.Ok(poll);
            },
            cancellationToken);

    /// <summary>
    /// The application-layer half of the unclaimed-link guard the ticket calls for: the aggregate's
    /// claimed-guest resolution is private, so this mirrors it read-only over the already-loaded
    /// (and now locked) aggregate's public <see cref="Poll.GuestLinks"/> collection.
    /// </summary>
    private static GuestPollCommandResult? RequireClaimedGuest(
        Poll poll,
        GuestLinkId guestLinkId,
        out ParticipantRef participant)
    {
        GuestLink? link = poll.GuestLinks.FirstOrDefault(l => l.Id == guestLinkId);
        if (link is null)
        {
            participant = default;
            return GuestPollCommandResult.Failed("poll_guest_link_not_found");
        }

        if (!link.IsActive)
        {
            participant = default;
            return GuestPollCommandResult.Failed("poll_guest_link_inactive");
        }

        if (link.GuestIdentityId is not { } guestIdentityId)
        {
            participant = default;
            return GuestPollCommandResult.Failed("guest_link_not_claimed");
        }

        participant = ParticipantRef.ForGuest(guestIdentityId);
        return null;
    }
}
