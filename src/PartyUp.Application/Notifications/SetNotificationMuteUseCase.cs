using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Notifications;

public sealed record MuteResult(bool Succeeded, string? Code)
{
    public static MuteResult Ok() => new(true, null);

    public static MuteResult Failed(string code) => new(false, code);
}

/// <summary>
/// PHASE7_DESIGN.md §4's mute write path: <c>SetNotificationMuteUseCase</c> +
/// <see cref="INotificationMuteRepository"/>. Who may mute = anyone the visibility engine grants
/// access to the target; no access ⇒ 404 (<c>session_not_found</c>/<c>poll_not_found</c>, unchanged).
/// Idempotent in both directions. Terminal-poll semantics: <c>MutePoll</c> on a terminal poll is
/// refused with the shipped <c>poll_invalid_transition</c> code — no new code; <c>UnmutePoll</c> on a
/// <c>Finalized</c> poll is not refused and removes the re-parented row via
/// <see cref="Poll.CreatedSessionId"/> (the same <c>Session.OriginPollId</c> linkage from the other
/// side); on an <c>Expired</c>/<c>Canceled</c> poll it removes the poll-keyed row directly.
/// </summary>
public sealed class SetNotificationMuteUseCase(
    ISessionRepository sessions,
    IPollRepository polls,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    IFriendGroupRepository groups,
    INotificationMuteRepository mutes,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<MuteResult> MuteSessionAsync(SessionId sessionId, UserId actorId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                Session? session = await sessions.FindNoTrackingAsync(sessionId, ct);
                if (session is null || !await IsVisibleSessionAsync(session, actorId, ct))
                {
                    return MuteResult.Failed("session_not_found");
                }

                if (!await mutes.ExistsForTargetAsync(actorId, sessionId, null, ct))
                {
                    await mutes.AddAsync(NotificationMute.ForSession(actorId, sessionId, clock), ct);
                }

                return MuteResult.Ok();
            },
            cancellationToken);

    public Task<MuteResult> UnmuteSessionAsync(SessionId sessionId, UserId actorId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                Session? session = await sessions.FindNoTrackingAsync(sessionId, ct);
                if (session is null || !await IsVisibleSessionAsync(session, actorId, ct))
                {
                    return MuteResult.Failed("session_not_found");
                }

                await mutes.RemoveForTargetAsync(actorId, sessionId, null, ct);
                return MuteResult.Ok();
            },
            cancellationToken);

    public Task<MuteResult> MutePollAsync(PollId pollId, UserId actorId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                Poll? poll = await polls.FindNoTrackingAsync(pollId, ct);
                if (poll is null || !await IsVisiblePollAsync(poll, actorId, ct))
                {
                    return MuteResult.Failed("poll_not_found");
                }

                if (poll.IsTerminal)
                {
                    return MuteResult.Failed("poll_invalid_transition");
                }

                if (!await mutes.ExistsForTargetAsync(actorId, null, pollId, ct))
                {
                    await mutes.AddAsync(NotificationMute.ForPoll(actorId, pollId, clock), ct);
                }

                return MuteResult.Ok();
            },
            cancellationToken);

    public Task<MuteResult> UnmutePollAsync(PollId pollId, UserId actorId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                Poll? poll = await polls.FindNoTrackingAsync(pollId, ct);
                if (poll is null || !await IsVisiblePollAsync(poll, actorId, ct))
                {
                    return MuteResult.Failed("poll_not_found");
                }

                if (poll.Status == PollStatus.Finalized && poll.CreatedSessionId is { } sessionId)
                {
                    // §4: no second, inert poll-keyed row — the mute re-parented onto the created
                    // session at finalization (§12-15), so removal reaches it through that link.
                    await mutes.RemoveForTargetAsync(actorId, sessionId, null, ct);
                }
                else
                {
                    await mutes.RemoveForTargetAsync(actorId, null, pollId, ct);
                }

                return MuteResult.Ok();
            },
            cancellationToken);

    private async Task<bool> IsVisibleSessionAsync(Session session, UserId viewerId, CancellationToken cancellationToken)
    {
        HashSet<UserId> counterparts = [session.OrganizerId];
        foreach (Participation participation in session.Participations)
        {
            if (participation.UserId is { } userId)
            {
                counterparts.Add(userId);
            }
        }

        Viewer viewer = await BuildViewerAsync(
            viewerId, counterparts, session.Visibility, session.AudienceGroupIds, cancellationToken);
        return VisibilityEngine.ResolveSession(session, viewer).IsVisible;
    }

    private async Task<bool> IsVisiblePollAsync(Poll poll, UserId viewerId, CancellationToken cancellationToken)
    {
        HashSet<UserId> counterparts = [poll.OrganizerId];
        foreach (PollParticipant participant in poll.Participants)
        {
            if (participant.UserId is { } userId)
            {
                counterparts.Add(userId);
            }
        }

        Viewer viewer = await BuildViewerAsync(
            viewerId, counterparts, poll.Visibility, poll.AudienceGroupIds, cancellationToken);
        return VisibilityEngine.ResolvePoll(poll, viewer).IsVisible;
    }

    private async Task<Viewer> BuildViewerAsync(
        UserId viewerId,
        HashSet<UserId> counterparts,
        ContentVisibility visibility,
        IReadOnlyCollection<FriendGroupId> audienceGroupIds,
        CancellationToken cancellationToken)
    {
        counterparts.Remove(viewerId);

        // §7 rule 2g (D-198). The mute write touches only the actor's own mute row, so this resolve
        // needs no group-row lock — it is the read side's answer, used to fail closed.
        IReadOnlyCollection<FriendGroupId> viewerGroupIds = await ViewerGroups.GrantingAsync(
            groups, viewerId, visibility, audienceGroupIds, cancellationToken);

        if (counterparts.Count == 0)
        {
            return Viewer.Create(viewerId, null, null, viewerGroupIds);
        }

        IReadOnlyList<Friendship> pairFriendships =
            await friendships.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);
        IReadOnlyList<Block> pairBlocks =
            await blocks.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);

        List<UserId> friendIds = [.. pairFriendships.Where(f => f.IsAccepted).Select(f => f.Counterpart(viewerId))];
        List<UserId> blockedIds = [.. pairBlocks.Select(b => b.BlockerId == viewerId ? b.BlockedId : b.BlockerId)];

        return Viewer.Create(viewerId, friendIds, blockedIds, viewerGroupIds);
    }
}
