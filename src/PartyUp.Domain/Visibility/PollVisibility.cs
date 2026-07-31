using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Visibility;

/// <summary>Projects a poll's voter roster onto the two §7 rule facts.</summary>
public static class PollVisibility
{
    /// <summary>
    /// §7 rule 1. A poll participant has no Pending state — an invitee is Active with no votes yet
    /// (§6.3) — so <c>Active</c> is the whole rule and <c>Removed</c> keeps nothing.
    /// </summary>
    public static bool KeepsDirectAccess(PollParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        return participant.IsActive;
    }

    /// <summary>
    /// §7 rule 2 / §12-17: an Active registered voter extends unless every current vote they hold is
    /// Unavailable — the poll analogue of Declined under D-051's mapping. A voter with no votes yet
    /// extends, like a pending invitee.
    /// </summary>
    public static bool ExtendsAccess(PollParticipant participant, bool votedUnavailableThroughout)
    {
        ArgumentNullException.ThrowIfNull(participant);

        return participant.IsRegistered && participant.IsActive && !votedUnavailableThroughout;
    }

    public static VisibilityTarget Describe(Poll poll)
    {
        ArgumentNullException.ThrowIfNull(poll);

        List<VisibilityMember> members = [];
        foreach (PollParticipant participant in poll.Participants)
        {
            if (participant.UserId is not { } userId)
            {
                continue;
            }

            members.Add(VisibilityMember.Create(
                userId,
                KeepsDirectAccess(participant),
                ExtendsAccess(participant, poll.VotedUnavailableThroughout(participant.Participant))));
        }

        List<UserId> requesters = [];
        foreach (JoinRequest request in poll.JoinRequests)
        {
            if (request.IsPending)
            {
                requesters.Add(request.RequesterId);
            }
        }

        return VisibilityTarget.Create(poll.OrganizerId, poll.IsPrivate, members, requesters);
    }
}
