using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Polls;

/// <summary>
/// The registered-viewer poll read (PHASE6_DESIGN.md §4 "GET /{id}"): resolves visibility over the
/// loaded aggregate exactly like <c>SessionReadUseCases.GetSessionDetailAsync</c>, then renders the
/// voter grid — per-option tallies, the viewer's own vote, and the roster through the D-076
/// blocked-pair masking rule. No use case in this file locks a row; it is a GET.
/// </summary>
public sealed class PollReadUseCases(
    IPollRepository polls,
    IPollReadRepository reads,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    INotificationMuteRepository mutes)
{
    public async Task<PollDetail?> GetPollDetailAsync(
        PollId pollId,
        UserId viewerId,
        CancellationToken cancellationToken)
    {
        Poll? poll = await polls.FindNoTrackingAsync(pollId, cancellationToken);
        if (poll is null)
        {
            return null;
        }

        Viewer viewer = await BuildViewerAsync(viewerId, poll, cancellationToken);
        VisibilityResult visibility = VisibilityEngine.ResolvePoll(poll, viewer);
        if (!visibility.IsVisible)
        {
            return null;
        }

        List<UserId> registeredIds = [poll.OrganizerId];
        foreach (PollParticipant participant in poll.Participants)
        {
            if (participant.UserId is { } userId)
            {
                registeredIds.Add(userId);
            }
        }

        IReadOnlyDictionary<Guid, UserNameplate> nameplates =
            await reads.FindNameplatesAsync(registeredIds, cancellationToken);

        List<PollRosterEntry> roster = [];
        Dictionary<ParticipantRef, PollRosterEntry> rosterByParticipant = [];
        HashSet<UserId> maskedUserIds = [];
        foreach (PollParticipant participant in poll.Participants)
        {
            DisplayName? displayName = participant.UserId is { } userId
                ? nameplates.GetValueOrDefault(userId.Value)?.DisplayName
                : participant.GuestIdentityId is { } guestId
                    ? poll.GuestIdentities.FirstOrDefault(g => g.Id == guestId)?.DisplayName
                    : null;

            // D-017: a guest has no avatar, so this resolves only for the registered arm.
            Avatar? avatar = participant.UserId is { } avatarUserId
                ? nameplates.GetValueOrDefault(avatarUserId.Value)?.Avatar
                : null;

            bool masked = participant.UserId is { } participantUserId &&
                viewer.BlockedCounterpartIds.Contains(participantUserId);

            if (masked && participant.UserId is { } maskedUserId)
            {
                maskedUserIds.Add(maskedUserId);
            }

            var rosterEntry = new PollRosterEntry(
                participant.Id,
                participant.Participant,
                masked ? null : displayName,
                participant.IsActive,
                masked,
                masked ? null : avatar);
            roster.Add(rosterEntry);
            rosterByParticipant[participant.Participant] = rosterEntry;
        }

        ParticipantRef viewerParticipant = ParticipantRef.ForUser(viewerId);
        List<PollOptionGridEntry> options = [];
        foreach (PollOption option in poll.Options)
        {
            int available = 0;
            int maybe = 0;
            int unavailable = 0;
            VoteChoice? ownVote = null;

            foreach (PollVote vote in poll.Votes)
            {
                if (vote.OptionId != option.Id)
                {
                    continue;
                }

                switch (vote.Choice)
                {
                    case VoteChoice.Available:
                        available++;
                        break;
                    case VoteChoice.Maybe:
                        maybe++;
                        break;
                    case VoteChoice.Unavailable:
                        unavailable++;
                        break;
                }

                if (vote.CastBy(viewerParticipant))
                {
                    ownVote = vote.Choice;
                }
            }

            bool suggesterMasked = option.SuggestedBy.UserId is { } suggesterId && maskedUserIds.Contains(suggesterId);

            // D-196: an organizer-created option carries no attribution (DOMAIN_MODEL §12-48);
            // otherwise the suggester renders through the identical roster row the voter lists use,
            // so its masking agrees with the roster without a second masking pass.
            PollRosterEntry? suggestedByEntry = option.SuggestedBy.UserId == poll.OrganizerId
                ? null
                : rosterByParticipant.GetValueOrDefault(option.SuggestedBy);

            var (availableVoters, maybeVoters, unavailableVoters) =
                PollVoterListBuilder.Build(option.Id, poll.Votes, rosterByParticipant);

            options.Add(new PollOptionGridEntry(
                option.Id, option.StartTime, option.SuggestedBy, suggesterMasked, suggestedByEntry,
                available, maybe, unavailable, ownVote,
                availableVoters, maybeVoters, unavailableVoters));
        }

        bool muted = poll.Status == PollStatus.Finalized && poll.CreatedSessionId is { } createdSessionId
            ? await mutes.ExistsForTargetAsync(viewerId, createdSessionId, null, cancellationToken)
            : await mutes.ExistsForTargetAsync(viewerId, null, poll.Id, cancellationToken);

        return new PollDetail(
            poll.Id,
            poll.OrganizerId,
            poll.Title,
            poll.Duration,
            poll.Platform,
            poll.Game,
            poll.Notes,
            poll.MinAttendance,
            poll.MaxCapacity,
            poll.Visibility == ContentVisibility.Private,
            poll.DeadlineChoice,
            poll.Deadline,
            poll.SelectionWindowEndsAt,
            poll.Status,
            poll.CreatedAt,
            poll.SelectedOptionId,
            poll.CreatedSessionId,
            options,
            roster,
            muted);
    }

    /// <summary>Mirrors <c>SessionReadUseCases.BuildViewerAsync</c>'s poll analogue: no row lock, a GET.</summary>
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

        if (counterparts.Count == 0)
        {
            return Viewer.Create(viewerId);
        }

        IReadOnlyList<Friendship> pairFriendships =
            await friendships.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);
        IReadOnlyList<Block> pairBlocks =
            await blocks.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);

        List<UserId> friendIds = [.. pairFriendships.Where(f => f.IsAccepted).Select(f => f.Counterpart(viewerId))];
        List<UserId> blockedIds = [.. pairBlocks.Select(b => b.BlockerId == viewerId ? b.BlockedId : b.BlockerId)];

        return Viewer.Create(viewerId, friendIds, blockedIds);
    }
}
