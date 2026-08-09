using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Polls;

/// <summary>What <see cref="GuestPollReadUseCases.GetGuestPollDetailAsync"/> renders alongside the shared <see cref="PollDetail"/> shape.</summary>
public sealed record GuestPollDetail(PollDetail Detail, GuestIdentityId? OwnGuestIdentityId, DisplayName? OwnDisplayName);

/// <summary>
/// A poll guest bearer's read (PHASE6_DESIGN.md §4, ticket-P6-S4b): "the same masked per-voter grid
/// data the registered view gets" — but for a guest viewer, masking is structurally a no-op, since
/// blocking is a relationship between <see cref="UserId"/>s and a guest is never one (D-030/D-114
/// participant-identical access, unlike the roster-less <c>GuestSessionResponse</c>). This is a
/// separate class from <see cref="PollReadUseCases"/> rather than a second overload on it: the
/// registered read's friendship/block dependencies have nothing to resolve for a guest viewer.
/// </summary>
public sealed class GuestPollReadUseCases(IPollRepository polls, IPollReadRepository reads)
{
    public async Task<GuestPollDetail?> GetGuestPollDetailAsync(
        PollId pollId,
        GuestLinkId guestLinkId,
        CancellationToken cancellationToken)
    {
        Poll? poll = await polls.FindNoTrackingAsync(pollId, cancellationToken);
        if (poll is null)
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

        IReadOnlyDictionary<Guid, DisplayName?> displayNames =
            await reads.FindDisplayNamesAsync(registeredIds, cancellationToken);

        List<PollRosterEntry> roster = [];
        foreach (PollParticipant participant in poll.Participants)
        {
            DisplayName? displayName = participant.UserId is { } userId
                ? displayNames.GetValueOrDefault(userId.Value)
                : participant.GuestIdentityId is { } guestId
                    ? poll.GuestIdentities.FirstOrDefault(g => g.Id == guestId)?.DisplayName
                    : null;

            // A guest viewer never blocks or is blocked (D-076 masking is a User-to-User relation
            // only), so MaskedForViewer stays false throughout — every row renders exactly as the
            // registered voter-grid view would for an unblocked viewer.
            roster.Add(new PollRosterEntry(participant.Id, participant.Participant, displayName, participant.IsActive));
        }

        GuestLink? link = poll.GuestLinks.FirstOrDefault(l => l.Id == guestLinkId);
        GuestIdentityId? claimedIdentityId = link?.GuestIdentityId;
        ParticipantRef? viewerParticipant = claimedIdentityId is { } claimedId
            ? ParticipantRef.ForGuest(claimedId)
            : null;
        DisplayName? ownDisplayName = claimedIdentityId is { } ownId
            ? poll.GuestIdentities.FirstOrDefault(g => g.Id == ownId)?.DisplayName
            : null;

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

                if (viewerParticipant is { } participant && vote.CastBy(participant))
                {
                    ownVote = vote.Choice;
                }
            }

            options.Add(new PollOptionGridEntry(
                option.Id, option.StartTime, option.SuggestedBy, false,
                available, maybe, unavailable, ownVote));
        }

        var detail = new PollDetail(
            poll.Id,
            poll.OrganizerId,
            poll.Title,
            poll.Duration,
            poll.Platform,
            poll.Game,
            poll.Notes,
            poll.MinAttendance,
            poll.MaxCapacity,
            poll.IsPrivate,
            poll.DeadlineChoice,
            poll.Deadline,
            poll.SelectionWindowEndsAt,
            poll.Status,
            poll.CreatedAt,
            poll.SelectedOptionId,
            poll.CreatedSessionId,
            options,
            roster,
            // D-167: a guest credential holds no channel at any layer, so this surface never mutes.
            Muted: false);

        return new GuestPollDetail(detail, claimedIdentityId, ownDisplayName);
    }
}
