using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Polls;

/// <summary>
/// D-225's poll arm, mirroring <c>UpgradeGuestSessionUseCase</c>: the poll parent is locked, the
/// guest credential's link must still be Active and claimed, a blocked pair with the organizer is a
/// generic refusal, and the merge itself is one aggregate call inside one transaction (A-016).
/// </summary>
public sealed class UpgradeGuestPollUseCase(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IBlockRepository blocks,
    IPollNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<GuestPollCommandResult> UpgradeAsync(
        PollId pollId,
        GuestLinkId guestLinkId,
        UserId userId,
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

                GuestLink? link = poll.GuestLinks.FirstOrDefault(l => l.Id == guestLinkId);
                if (link is null)
                {
                    return GuestPollCommandResult.Failed("poll_not_found");
                }

                if (poll.Status == PollStatus.Finalized)
                {
                    return GuestPollCommandResult.Failed("poll_terminal");
                }

                // A revoked link AND a canceled or expired poll both land on a notice; neither is
                // upgradeable — the same resolution the guest's own reads go through.
                if (poll.ResolveGuestLink(guestLinkId, clock) != GuestLinkView.LiveView)
                {
                    return GuestPollCommandResult.Failed("guest_link_terminal");
                }

                if (link.GuestIdentityId is not { } guestIdentityId)
                {
                    return GuestPollCommandResult.Failed("guest_link_unclaimed");
                }

                IReadOnlyList<Block> pair = await blocks.FindPairAsync(userId, poll.OrganizerId, ct);
                if (pair.Count > 0)
                {
                    return GuestPollCommandResult.Failed("upgrade_unavailable");
                }

                poll.UpgradeGuest(guestIdentityId, userId, clock);

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return GuestPollCommandResult.Ok(poll);
            },
            cancellationToken);
}
