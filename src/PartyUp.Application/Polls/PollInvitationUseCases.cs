using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Application.Polls;

/// <summary>
/// D-193's post-creation registered invitations, mirroring
/// <see cref="Sessions.SessionInvitationUseCases.InviteRegisteredUsersAsync"/> step-for-step
/// (DOMAIN_MODEL.md §6.3 "Mid-flight invite"): probe for organizer authorization without locking
/// arbitrary invitee ids, lock the affected user set in one statement, lock friendship/block rows one
/// statement per table (A-068), lock the poll parent and reload its children, then re-evaluate
/// eligibility under the fresh locks. Invite is all-or-nothing — a single unusable invitee never
/// partially attaches the rest and never discloses which invitee was blocked (uniform
/// <c>user_not_found</c>).
/// </summary>
public sealed class PollInvitationUseCases(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    IPollNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock,
    InviteEmailService inviteEmails)
{
    public const int MaxInviteeCount = 50;

    public async Task<PollCommandResult> InviteRegisteredUsersAsync(
        PollId pollId,
        UserId organizerId,
        IReadOnlyList<UserId> inviteeIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inviteeIds);

        List<UserId> invitees = [.. inviteeIds.Distinct()];
        if (invitees.Count is 0 or > MaxInviteeCount)
        {
            return PollCommandResult.Failed("poll_validation");
        }

        IReadOnlyList<IDomainEvent> committedEvents = [];
        PollCommandResult result = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                // Step 1: resolve authorization without locking the arbitrary invitee ids; hidden/missing -> 404.
                Poll? probe = await polls.FindNoTrackingAsync(pollId, ct);
                if (probe is null || probe.OrganizerId != organizerId)
                {
                    return PollCommandResult.Failed("poll_not_found");
                }

                // Step 2: lock the complete affected registered-user set in one statement.
                HashSet<UserId> lockSet = [organizerId, .. invitees];
                await pollLocks.LockUsersAsync(lockSet, ct);

                // Step 3: lock relevant friendship rows and both block directions, one statement per
                // table -- never a per-invitee loop (A-068).
                IReadOnlyList<Friendship> lockedFriendships = await friendships.LockPairsAsync(organizerId, invitees, ct);
                IReadOnlyList<Block> lockedBlocks = await blocks.LockPairsAsync(organizerId, invitees, ct);

                // Step 4: lock the poll parent, then load its children.
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

                // Step 5: re-evaluate friendship/block eligibility under the fresh locks. Invite is
                // all-or-nothing: any unusable invitee fails the whole command before anything mutates.
                Dictionary<UserId, bool> blockedByInvitee = [];
                foreach (UserId inviteeId in invitees)
                {
                    bool isFriend = lockedFriendships.Any(f => f.IsBetween(organizerId, inviteeId) && f.IsAccepted);
                    bool blocked = lockedBlocks.Any(b => b.Hides(organizerId, inviteeId));
                    blockedByInvitee[inviteeId] = blocked;

                    if (!isFriend || blocked)
                    {
                        return PollCommandResult.Failed("user_not_found");
                    }
                }

                foreach (UserId inviteeId in invitees)
                {
                    poll.Invite(inviteeId, clock, id => blockedByInvitee.GetValueOrDefault(id));
                }

                // Step 6: persist.
                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                committedEvents = events;
                await materializer.MaterializeAsync(poll, events, ct);

                return PollCommandResult.Ok(poll);
            },
            cancellationToken);

        if (result.Succeeded && result.Poll is { } invitedPoll)
        {
            await inviteEmails.NotifyPollEventsAsync(invitedPoll, committedEvents, cancellationToken);
        }

        return result;
    }
}
