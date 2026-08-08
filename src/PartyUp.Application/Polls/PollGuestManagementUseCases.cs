using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Polls;

public sealed record CreatePollGuestLinksResult(bool Succeeded, string? Code, IReadOnlyList<CreatedGuestLink>? GuestLinks)
{
    public static CreatePollGuestLinksResult Ok(IReadOnlyList<CreatedGuestLink> links) => new(true, null, links);

    public static CreatePollGuestLinksResult Failed(string code) => new(false, code, null);
}

/// <summary>
/// The organizer's guest-lifecycle surface (PHASE6_DESIGN.md §2 "Mint / revoke / remove guests"):
/// minting (<c>Polling</c>-only and pre-deadline, aggregate-enforced), revoking an unclaimed link
/// (a claimed link surfaces the aggregate's Conflict instead), and removing a claimed guest — voter
/// + link revocation in one command (D-044). Renaming is guest-side, in
/// <see cref="GuestPollUseCases"/>.
/// </summary>
public sealed class PollGuestManagementUseCases(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IPollNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public const int MaxGuestLinkCount = 20;

    public Task<CreatePollGuestLinksResult> CreateGuestLinksAsync(
        PollId pollId,
        UserId organizerId,
        int count,
        CancellationToken cancellationToken)
    {
        if (count is < 1 or > MaxGuestLinkCount)
        {
            return Task.FromResult(CreatePollGuestLinksResult.Failed("poll_validation"));
        }

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                await pollLocks.LockUsersAsync([organizerId], ct);
                await pollLocks.LockPollsAsync([pollId], ct);

                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return CreatePollGuestLinksResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                if (poll.OrganizerId != organizerId)
                {
                    return CreatePollGuestLinksResult.Failed("poll_not_found");
                }

                List<CreatedGuestLink> links = [];
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        (GuestLink link, string token) = poll.CreateGuestLink(clock);
                        links.Add(new CreatedGuestLink(link.Id, token));
                    }
                }
                catch (DomainException exception)
                {
                    return CreatePollGuestLinksResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return CreatePollGuestLinksResult.Ok(links);
            },
            cancellationToken);
    }

    /// <summary>D-150: an unclaimed link revokes on its own; a claimed one surfaces the aggregate's Conflict.</summary>
    public Task<PollCommandResult> RevokeGuestLinkAsync(
        PollId pollId,
        UserId organizerId,
        GuestLinkId guestLinkId,
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

                GuestLink? link = poll.GuestLinks.FirstOrDefault(l => l.Id == guestLinkId);
                if (link is not null && !link.IsActive)
                {
                    // Idempotent: an already-revoked link has no roster effect either way.
                    return PollCommandResult.Ok(poll);
                }

                try
                {
                    poll.RevokeGuestLink(guestLinkId, clock);
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

    /// <summary>D-044: removes the claimed voter and revokes every link pointing at their identity, one command.</summary>
    public Task<PollCommandResult> RemoveGuestAsync(
        PollId pollId,
        UserId organizerId,
        GuestIdentityId guestIdentityId,
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

                if (!poll.GuestIdentities.Any(identity => identity.Id == guestIdentityId))
                {
                    return PollCommandResult.Failed("poll_guest_not_found");
                }

                try
                {
                    poll.RemoveGuest(guestIdentityId, clock);
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
