using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Polls;

public sealed record PollJoinRequestCommandResult(bool Succeeded, string? Code, Poll? Poll, JoinRequest? JoinRequest)
{
    public static PollJoinRequestCommandResult Ok(Poll poll, JoinRequest? request = null) =>
        new(true, null, poll, request);

    public static PollJoinRequestCommandResult Failed(string code) => new(false, code, null, null);
}

/// <summary>
/// Request/withdraw/approve/reject (D-118), mirroring <see cref="SessionJoinRequestUseCases"/>
/// (PHASE6_DESIGN.md §2 "Join requests"). <see cref="RequestJoinAsync"/> and
/// <see cref="ApproveJoinRequestAsync"/> attach a requester to the poll (a new registered
/// counterpart), so both run the full P3-LK-01 sequence: resolve visibility/authorization without
/// locking arbitrary ids, lock the affected user set in one statement, lock the organizer/requester
/// friendship and block rows in one statement per table, lock the poll parent and reload its
/// children, then re-evaluate visibility with the same <see cref="VisibilityEngine"/> the read side
/// would use before persisting — a blocked pair (organizer or any §7-rule-2 extender) returns the
/// same <c>poll_not_found</c> a stranger gets (D-065's poll analogue), indistinguishable and writing
/// nothing. Withdraw/reject act on an already-known requester and introduce no new counterpart, so
/// they keep the poll-parent-lock-before-occupancy shape without the graph lock (reject still
/// re-checks the block pair, so a block-first race also yields the indistinguishable 404 there).
/// </summary>
public sealed class PollJoinRequestUseCases(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    IFriendGroupRepository groups,
    IPollNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock,
    InviteEmailService inviteEmails)
{
    public Task<PollJoinRequestCommandResult> RequestJoinAsync(
        PollId pollId,
        UserId requesterId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                // Step 1: resolve authorization without locking arbitrary ids; hidden/missing -> 404.
                Poll? probe = await polls.FindNoTrackingAsync(pollId, ct);
                if (probe is null || requesterId == probe.OrganizerId)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                Viewer earlyViewer = await BuildViewerAsync(requesterId, probe, ct);
                if (!VisibilityEngine.ResolvePoll(probe, earlyViewer).IsVisible)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                UserId organizerId = probe.OrganizerId;

                // Step 2: lock the complete affected registered-user set in one statement.
                await pollLocks.LockUsersAsync([organizerId, requesterId], ct);

                // Step 3: lock relevant friendship rows and both block directions, one statement per table.
                IReadOnlyList<Friendship> lockedFriendships =
                    await friendships.LockPairsAsync(organizerId, [requesterId], ct);
                IReadOnlyList<Block> lockedBlocks = await blocks.LockPairsAsync(organizerId, [requesterId], ct);

                // Step 4: lock the poll parent, then load its children.
                await pollLocks.LockPollsAsync([pollId], ct);
                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;

                // Step 5: re-evaluate visibility under the fresh locks (§7 rule 2 via ANY extending
                // active registered participant; the roster is only known once the parent lock loaded
                // it, so a second bulk graph-lock statement, one per table, covers the requester paired
                // with every extender).
                HashSet<UserId> extenderIds = [];
                foreach (PollParticipant participant in poll.Participants)
                {
                    if (participant.UserId is { } participantId
                        && participantId != requesterId
                        && participantId != organizerId
                        && PollVisibility.ExtendsAccess(participant, poll.VotedUnavailableThroughout(participant.Participant)))
                    {
                        extenderIds.Add(participantId);
                    }
                }

                IReadOnlyList<Friendship> extenderFriendships = extenderIds.Count == 0
                    ? []
                    : await friendships.LockPairsAsync(requesterId, extenderIds, ct);
                IReadOnlyList<Block> extenderBlocks = extenderIds.Count == 0
                    ? []
                    : await blocks.LockPairsAsync(requesterId, extenderIds, ct);

                // Rule 2g (D-198) resolves only here, under the parent lock: an audience edit racing
                // this request must not be able to make the membership answer stale before commit.
                IReadOnlyCollection<FriendGroupId> viewerGroupIds = await ViewerGroups.LockGrantingAsync(
                    groups, requesterId, poll.Visibility, poll.AudienceGroupIds, ct);

                Viewer viewer = ViewerGroups.With(
                    Viewer.From(
                        requesterId,
                        [.. lockedFriendships, .. extenderFriendships],
                        [.. lockedBlocks, .. extenderBlocks]),
                    viewerGroupIds);
                if (!VisibilityEngine.ResolvePoll(poll, viewer).IsVisible)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                JoinRequest request;
                try
                {
                    request = poll.OpenJoinRequest(requesterId, clock);
                }
                catch (DomainException exception)
                {
                    return PollJoinRequestCommandResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                // Step 6: persist.
                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return PollJoinRequestCommandResult.Ok(poll, request);
            },
            cancellationToken);

    /// <summary>
    /// Resolves through <see cref="PollActorVisibilityGate"/> before any domain call -- a stranger or
    /// blocked requester gets the same <c>poll_not_found</c> a missing poll gives.
    /// </summary>
    public Task<PollJoinRequestCommandResult> WithdrawJoinRequestAsync(
        PollId pollId,
        UserId requesterId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                PollForUpdate? forUpdate = await PollActorVisibilityGate.LoadVisibleForUpdateAsync(
                    pollId, requesterId, polls, pollLocks, friendships, blocks, groups, ct);
                if (forUpdate is null)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                JoinRequest? request = poll.JoinRequests
                    .FirstOrDefault(r => r.RequesterId == requesterId && r.IsPending);

                if (request is null)
                {
                    // D-107: withdrawal is idempotent -- no pending request is simply a no-op.
                    return PollJoinRequestCommandResult.Ok(poll);
                }

                poll.WithdrawJoinRequest(request.Id, clock);
                _ = poll.DequeueEvents();

                return PollJoinRequestCommandResult.Ok(poll, request);
            },
            cancellationToken);

    public async Task<PollJoinRequestCommandResult> ApproveJoinRequestAsync(
        PollId pollId,
        UserId organizerId,
        JoinRequestId joinRequestId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IDomainEvent> committedEvents = [];
        PollJoinRequestCommandResult result = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                Poll? probe = await polls.FindNoTrackingAsync(pollId, ct);
                if (probe is null || probe.OrganizerId != organizerId)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                JoinRequest? probeRequest = probe.JoinRequests.FirstOrDefault(r => r.Id == joinRequestId);
                if (probeRequest is null)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                UserId requesterId = probeRequest.RequesterId;

                await pollLocks.LockUsersAsync([organizerId, requesterId], ct);
                IReadOnlyList<Block> lockedBlocks = await blocks.LockPairsAsync(organizerId, [requesterId], ct);

                await pollLocks.LockPollsAsync([pollId], ct);
                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                if (poll.OrganizerId != organizerId)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                // Block-first (or a since-vanished poll) is indistinguishable from missing -- zero
                // writes, zero notifications (D-065's poll analogue).
                if (lockedBlocks.Any(b => b.Hides(organizerId, requesterId)))
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                JoinRequest? request = poll.JoinRequests.FirstOrDefault(r => r.Id == joinRequestId);
                if (request is null)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                if (!request.IsPending)
                {
                    return PollJoinRequestCommandResult.Failed("join_request_not_pending");
                }

                try
                {
                    poll.ApproveJoinRequest(joinRequestId, clock);
                }
                catch (DomainException exception)
                {
                    return PollJoinRequestCommandResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                committedEvents = events;
                await materializer.MaterializeAsync(poll, events, ct);

                return PollJoinRequestCommandResult.Ok(poll, request);
            },
            cancellationToken);

        if (result.Succeeded && result.Poll is { } approvedPoll)
        {
            await inviteEmails.NotifyPollEventsAsync(approvedPoll, committedEvents, cancellationToken);
        }

        return result;
    }

    public Task<PollJoinRequestCommandResult> RejectJoinRequestAsync(
        PollId pollId,
        UserId organizerId,
        JoinRequestId joinRequestId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                Poll? probe = await polls.FindNoTrackingAsync(pollId, ct);
                if (probe is null || probe.OrganizerId != organizerId)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                JoinRequest? probeRequest = probe.JoinRequests.FirstOrDefault(r => r.Id == joinRequestId);
                if (probeRequest is null)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                UserId requesterId = probeRequest.RequesterId;

                await pollLocks.LockUsersAsync([organizerId, requesterId], ct);
                IReadOnlyList<Block> lockedBlocks = await blocks.LockPairsAsync(organizerId, [requesterId], ct);

                await pollLocks.LockPollsAsync([pollId], ct);
                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                if (poll.OrganizerId != organizerId)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                if (lockedBlocks.Any(b => b.Hides(organizerId, requesterId)))
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                JoinRequest? request = poll.JoinRequests.FirstOrDefault(r => r.Id == joinRequestId);
                if (request is null)
                {
                    return PollJoinRequestCommandResult.Failed("poll_not_found");
                }

                if (!request.IsPending)
                {
                    return PollJoinRequestCommandResult.Failed("join_request_not_pending");
                }

                // §1: RejectJoinRequest applies due transitions first (Poll.cs:652) -- after any close
                // the request is already Lapsed and this surfaces as the invalid-transition error
                // rather than a leaked exception, exactly as the design's "no after-close reject
                // semantics" ruling requires.
                try
                {
                    poll.RejectJoinRequest(joinRequestId, clock);
                }
                catch (DomainException exception)
                {
                    return PollJoinRequestCommandResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return PollJoinRequestCommandResult.Ok(poll);
            },
            cancellationToken);

    /// <summary>
    /// The minimal, non-locking §7 viewer facts a pre-lock visibility resolve needs: the organizer
    /// plus every other registered participant, read in two bulk queries — mirrors
    /// <see cref="SessionJoinRequestUseCases"/>'s viewer builder.
    /// </summary>
    private async Task<Viewer> BuildViewerAsync(UserId viewerId, Poll poll, CancellationToken cancellationToken)
    {
        HashSet<UserId> counterparts = [poll.OrganizerId];
        foreach (PollParticipant participant in poll.Participants)
        {
            if (participant.UserId is { } userId && userId != viewerId)
            {
                counterparts.Add(userId);
            }
        }

        counterparts.Remove(viewerId);

        IReadOnlyCollection<FriendGroupId> viewerGroupIds = await ViewerGroups.GrantingAsync(
            groups, viewerId, poll.Visibility, poll.AudienceGroupIds, cancellationToken);

        if (counterparts.Count == 0)
        {
            return Viewer.Create(viewerId, null, null, viewerGroupIds);
        }

        IReadOnlyList<Friendship> pairFriendships = await friendships.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);
        IReadOnlyList<Block> pairBlocks = await blocks.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);

        List<UserId> friendIds = [.. pairFriendships.Where(f => f.IsAccepted).Select(f => f.Counterpart(viewerId))];
        List<UserId> blockedIds = [.. pairBlocks.Select(b => b.BlockerId == viewerId ? b.BlockedId : b.BlockerId)];

        return Viewer.Create(viewerId, friendIds, blockedIds, viewerGroupIds);
    }
}
