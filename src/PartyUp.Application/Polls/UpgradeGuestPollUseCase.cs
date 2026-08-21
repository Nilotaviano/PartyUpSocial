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
                // A-068 lock order (§6.1, the SessionJoinRequestUseCases convention): a non-locking
                // probe names the affected registered users, those rows and the block pair are
                // locked, and only then is the poll parent locked and loaded.
                Poll? probe = await polls.FindNoTrackingAsync(pollId, ct);
                if (probe is null)
                {
                    return GuestPollCommandResult.Failed("poll_not_found");
                }

                UserId organizerId = probe.OrganizerId;
                await pollLocks.LockUsersAsync([organizerId, userId], ct);

                // The blocked-pair lock is acquired here to preserve the users -> pairs -> parent
                // lock order, but the decision it feeds is deferred past the terminal/unclaimed
                // checks below so a terminal or unclaimed link never answers "upgrade_unavailable".
                IReadOnlyList<Block> pair = await blocks.LockPairsAsync(userId, [organizerId], ct);

                await pollLocks.LockPollsAsync([pollId], ct);

                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return GuestPollCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;

                // Checked on the poll itself, ahead of the guest-link lookup: finalization hands the
                // poll's guest links to the newly created session and clears them from this
                // collection (Poll.Finalize), so a link-first check would never see this branch.
                if (poll.Status == PollStatus.Finalized)
                {
                    return GuestPollCommandResult.Failed("poll_terminal");
                }

                GuestLink? link = poll.GuestLinks.FirstOrDefault(l => l.Id == guestLinkId);
                if (link is null)
                {
                    return GuestPollCommandResult.Failed("poll_not_found");
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

                if (pair.Count > 0)
                {
                    return GuestPollCommandResult.Failed("upgrade_unavailable");
                }

                // The duplicate branch severs the guest participant and its votes from the
                // aggregate; both FKs are required and non-cascading, so the deletion is handed to
                // the repository explicitly (the D-186 self-removal idiom).
                PollGuestUpgradeResult upgrade = poll.UpgradeGuest(guestIdentityId, userId, clock);
                if (upgrade.RemovedParticipant is { } removedParticipant)
                {
                    polls.RemoveParticipationAndWithdrawnVotes(removedParticipant, upgrade.RemovedVotes);
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return GuestPollCommandResult.Ok(poll);
            },
            cancellationToken);
}
