using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Visibility;

/// <summary>Projects a session's roster onto the two §7 rule facts.</summary>
public static class SessionVisibility
{
    /// <summary>
    /// §7 rule 1. <c>Removed</c> is terminal and keeps nothing (§5.2). <c>Origin = Invited</c> keeps
    /// access at any surviving status — a Declined invitee keeps seeing what they were invited to
    /// (D-019) — while a voluntarily departed non-invitee does not (D-043/D-047).
    /// </summary>
    public static bool KeepsDirectAccess(Participation participation)
    {
        ArgumentNullException.ThrowIfNull(participation);

        return !participation.IsRemoved
            && (participation.Origin == ParticipationOrigin.Invited
                || participation.Status is ParticipationStatus.Pending
                    or ParticipationStatus.Going
                    or ParticipationStatus.Maybe);
    }

    /// <summary>
    /// §7 rule 2, D-018: status-only and origin-irrelevant, so a revived or poll-finalized
    /// <c>Pending</c> row extends exactly like an invited one. Waitlisted rows keep the Going/Maybe
    /// they selected (D-084) and extend with it. Declined, Removed and guest rows never extend; the
    /// organizer extends by identity even while Declined (D-085), which the engine applies.
    /// </summary>
    public static bool ExtendsAccess(Participation participation)
    {
        ArgumentNullException.ThrowIfNull(participation);

        return participation.IsRegistered
            && !participation.IsRemoved
            && participation.Status is ParticipationStatus.Pending
                or ParticipationStatus.Going
                or ParticipationStatus.Maybe;
    }

    public static VisibilityTarget Describe(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        List<VisibilityMember> members = [];
        foreach (Participation participation in session.Participations)
        {
            if (participation.UserId is not { } userId)
            {
                continue;
            }

            members.Add(VisibilityMember.Create(
                userId,
                KeepsDirectAccess(participation),
                ExtendsAccess(participation)));
        }

        List<UserId> requesters = [];
        foreach (JoinRequest request in session.JoinRequests)
        {
            if (request.IsPending)
            {
                requesters.Add(request.RequesterId);
            }
        }

        return VisibilityTarget.Create(session.OrganizerId, session.IsPrivate, members, requesters);
    }
}
