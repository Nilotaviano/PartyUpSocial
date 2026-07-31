using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Notifications;

/// <summary>
/// The §10 event → recipient contracts, one function per spec §8.4 row (D-112), as pure functions
/// over the aggregate that raised the event plus the write-time <see cref="RecipientFilter"/>.
/// </summary>
/// <remarks>
/// Three rules cut across every row. Guests are never recipients (D-167) — they see state by
/// revisiting their link. Mute suppresses the record itself, not merely its push (D-090/D-113), the
/// reminder excepted (<see cref="ReminderRules"/>). And some legitimate recipients hold no
/// participation at all, or just lost their access to the very command being reported: a rejected
/// requester and a waitlist-rejected participant are <em>explicit targets</em> taken from the event,
/// not members of a visibility set, which is why their rows still reach them.
/// </remarks>
public static class RecipientComputer
{
    // ---- §10 audiences --------------------------------------------------------------------------

    /// <summary>
    /// The status-scoped broadcast audience (§10): registered participants whose status is Pending,
    /// Going or Maybe — waitlisted included, since a waitlisted row keeps the status it selected
    /// (D-084) — plus the organizer, whose own attendance is irrelevant (D-085). The acting user is
    /// deliberately included: confirmation, unconfirmation and rescheduling are state-change
    /// broadcasts (§12-31).
    /// </summary>
    public static IReadOnlyList<UserId> StateChangeAudience(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        List<UserId> audience = [session.OrganizerId];

        foreach (Participation row in session.Participations)
        {
            if (row.UserId is { } userId
                && userId != session.OrganizerId
                && !row.IsRemoved
                && row.Status is ParticipationStatus.Pending
                    or ParticipationStatus.Going
                    or ParticipationStatus.Maybe)
            {
                audience.Add(userId);
            }
        }

        return audience;
    }

    /// <summary>
    /// The access-scoped cancellation audience (§10, D-166): every registered user holding §7 rule-1
    /// access <em>through a participation</em> — a Declined invitee included (D-019) — minus the
    /// organizer, who is not a remaining participant of their own cancellation (§12-31). Pending
    /// requesters hold rule-1 access through a request, not a participation, and are not in scope.
    /// </summary>
    public static IReadOnlyList<UserId> CancellationAudience(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        List<UserId> audience = [];

        foreach (Participation row in session.Participations)
        {
            if (row.UserId is { } userId
                && userId != session.OrganizerId
                && SessionVisibility.KeepsDirectAccess(row))
            {
                audience.Add(userId);
            }
        }

        return audience;
    }

    /// <summary>
    /// The poll broadcast audience (§10, D-120/D-166/D-170): every Active registered poll
    /// participant plus the organizer. A <c>Removed</c> voter (block D-077, deletion D-092) is out,
    /// exactly as they are out of the finalization mapping.
    /// </summary>
    public static IReadOnlyList<UserId> PollAudience(Poll poll)
    {
        ArgumentNullException.ThrowIfNull(poll);

        List<UserId> audience = [poll.OrganizerId];

        foreach (PollParticipant participant in poll.Participants)
        {
            if (participant.UserId is { } userId && userId != poll.OrganizerId && participant.IsActive)
            {
                audience.Add(userId);
            }
        }

        return audience;
    }

    /// <summary>The poll cancellation audience: <see cref="PollAudience"/> minus the canceling organizer (§12-31).</summary>
    public static IReadOnlyList<UserId> PollCancellationAudience(Poll poll)
    {
        ArgumentNullException.ThrowIfNull(poll);

        List<UserId> audience = [];

        foreach (UserId userId in PollAudience(poll))
        {
            if (userId != poll.OrganizerId)
            {
                audience.Add(userId);
            }
        }

        return audience;
    }

    // ---- §10 session rows -----------------------------------------------------------------------

    /// <summary>The new invitee only.</summary>
    public static IReadOnlyList<NotificationRequest> Invitation(
        SessionInvitationSent domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            domainEvent.InviteeId,
            NotificationType.SessionInvitation,
            NotificationSubject.ForSession(domainEvent.SessionId),
            filter);
    }

    public static IReadOnlyList<NotificationRequest> Confirmed(
        Session session,
        SessionConfirmed domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return Broadcast(
            StateChangeAudience(session),
            NotificationType.SessionConfirmed,
            NotificationSubject.ForSession(domainEvent.SessionId),
            filter);
    }

    public static IReadOnlyList<NotificationRequest> Unconfirmed(
        Session session,
        SessionUnconfirmed domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return Broadcast(
            StateChangeAudience(session),
            NotificationType.SessionUnconfirmed,
            NotificationSubject.ForSession(domainEvent.SessionId),
            filter);
    }

    /// <summary>
    /// Start-time (D-063) or duration (D-122) change. For a start-time reset the aggregate raises the
    /// event <em>after</em> returning every non-removed row to Pending, so the revived invitees this
    /// audience picks up are exactly the ones the reset revived (§5.6).
    /// </summary>
    public static IReadOnlyList<NotificationRequest> Rescheduled(
        Session session,
        SessionRescheduled domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return Broadcast(
            StateChangeAudience(session),
            NotificationType.SessionRescheduled,
            NotificationSubject.ForSession(domainEvent.SessionId),
            filter);
    }

    public static IReadOnlyList<NotificationRequest> Canceled(
        Session session,
        SessionCanceled domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return Broadcast(
            CancellationAudience(session),
            NotificationType.SessionCanceled,
            NotificationSubject.ForSession(domainEvent.SessionId, domainEvent.OrganizerId),
            filter);
    }

    /// <summary>D-107: the organizer, who is the one who can act on it.</summary>
    public static IReadOnlyList<NotificationRequest> JoinRequestReceived(
        Session session,
        SessionJoinRequestReceived domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            session.OrganizerId,
            NotificationType.SessionJoinRequestReceived,
            NotificationSubject.ForSession(domainEvent.SessionId, domainEvent.RequesterId),
            filter);
    }

    public static IReadOnlyList<NotificationRequest> JoinRequestAccepted(
        SessionJoinRequestAccepted domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            domainEvent.RequesterId,
            NotificationType.SessionJoinRequestAccepted,
            NotificationSubject.ForSession(domainEvent.SessionId),
            filter);
    }

    /// <summary>D-107: the rejected requester holds no participation at all — an explicit target.</summary>
    public static IReadOnlyList<NotificationRequest> JoinRequestRejected(
        SessionJoinRequestRejected domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            domainEvent.RequesterId,
            NotificationType.SessionJoinRequestRejected,
            NotificationSubject.ForSession(domainEvent.SessionId),
            filter);
    }

    public static IReadOnlyList<NotificationRequest> WaitlistAdded(
        SessionWaitlistAdded domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            domainEvent.Participant,
            NotificationType.WaitlistAdded,
            NotificationSubject.ForSession(domainEvent.SessionId),
            filter);
    }

    public static IReadOnlyList<NotificationRequest> WaitlistPromoted(
        SessionWaitlistPromoted domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            domainEvent.Participant,
            NotificationType.WaitlistPromoted,
            NotificationSubject.ForSession(domainEvent.SessionId),
            filter);
    }

    /// <summary>
    /// The §10 carve-out: this must reach the participant whose row the very same command turned
    /// <c>Removed</c>, so it is taken from the event rather than from the roster.
    /// </summary>
    public static IReadOnlyList<NotificationRequest> WaitlistRejected(
        SessionWaitlistRejected domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            domainEvent.Participant,
            NotificationType.WaitlistRejected,
            NotificationSubject.ForSession(domainEvent.SessionId),
            filter);
    }

    /// <summary>A vacancy needs organizer action — promotion is manual (spec §8.1).</summary>
    public static IReadOnlyList<NotificationRequest> WaitlistVacancy(
        Session session,
        SessionWaitlistVacancy domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            session.OrganizerId,
            NotificationType.WaitlistVacancy,
            NotificationSubject.ForSession(domainEvent.SessionId),
            filter);
    }

    // ---- §10 poll rows --------------------------------------------------------------------------

    public static IReadOnlyList<NotificationRequest> Invitation(
        PollInvitationSent domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            domainEvent.InviteeId,
            NotificationType.PollInvitation,
            NotificationSubject.ForPoll(domainEvent.PollId),
            filter);
    }

    /// <summary>
    /// D-120: the organizer only — voters are never told about each other's suggestions. The subject
    /// carries the suggested instant so the row renders without a denormalized time string (§9).
    /// </summary>
    public static IReadOnlyList<NotificationRequest> OptionSuggested(
        Poll poll,
        PollOptionSuggested domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            poll.OrganizerId,
            NotificationType.PollOptionSuggested,
            NotificationSubject.ForPollOption(
                domainEvent.PollId,
                poll.FindOption(domainEvent.OptionId)?.StartTime,
                domainEvent.SuggestedBy.UserId),
            filter);
    }

    /// <summary>
    /// D-120, at Deadline − 1 h. Scheduled rather than transitional, so it takes the clock and skips
    /// itself when the poll has left <c>Polling</c> by the time it is due (<see cref="PollDeadlineNotice"/>).
    /// </summary>
    public static IReadOnlyList<NotificationRequest> DeadlineApproaching(
        Poll poll,
        RecipientFilter filter,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(poll);

        if (!PollDeadlineNotice.ShouldSend(poll, clock))
        {
            return [];
        }

        return Broadcast(
            PollAudience(poll),
            NotificationType.PollDeadlineApproaching,
            NotificationSubject.ForPoll(poll.Id),
            filter);
    }

    public static IReadOnlyList<NotificationRequest> Closed(
        Poll poll,
        PollClosed domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return Broadcast(
            PollAudience(poll),
            NotificationType.PollClosed,
            NotificationSubject.ForPoll(domainEvent.PollId),
            filter);
    }

    /// <summary>D-170: expiry without a selection notifies every registered poll participant.</summary>
    public static IReadOnlyList<NotificationRequest> Expired(
        Poll poll,
        PollExpired domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return Broadcast(
            PollAudience(poll),
            NotificationType.PollExpired,
            NotificationSubject.ForPoll(domainEvent.PollId),
            filter);
    }

    public static IReadOnlyList<NotificationRequest> Canceled(
        Poll poll,
        PollCanceled domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return Broadcast(
            PollCancellationAudience(poll),
            NotificationType.PollCanceled,
            NotificationSubject.ForPoll(domainEvent.PollId, domainEvent.OrganizerId),
            filter);
    }

    /// <summary>§6.4-8/§12-16: fired in every finalization branch, the Expired one included.</summary>
    public static IReadOnlyList<NotificationRequest> OptionSelected(
        Poll poll,
        PollOptionSelected domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(domainEvent);

        return Broadcast(
            PollAudience(poll),
            NotificationType.PollOptionSelected,
            NotificationSubject.ForSelectedOption(
                domainEvent.PollId,
                domainEvent.SessionId,
                poll.FindOption(domainEvent.OptionId)?.StartTime),
            filter);
    }

    /// <summary>§6.4-8: "added to waitlist" for each participant the D-119 overflow waitlisted.</summary>
    public static IReadOnlyList<NotificationRequest> WaitlistAdded(
        PollWaitlistAdded domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            domainEvent.Participant,
            NotificationType.WaitlistAdded,
            NotificationSubject.Create(domainEvent.SessionId, domainEvent.PollId),
            filter);
    }

    public static IReadOnlyList<NotificationRequest> JoinRequestReceived(
        Poll poll,
        PollJoinRequestReceived domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            poll.OrganizerId,
            NotificationType.PollJoinRequestReceived,
            NotificationSubject.ForPoll(domainEvent.PollId, domainEvent.RequesterId),
            filter);
    }

    public static IReadOnlyList<NotificationRequest> JoinRequestAccepted(
        PollJoinRequestAccepted domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            domainEvent.RequesterId,
            NotificationType.PollJoinRequestAccepted,
            NotificationSubject.ForPoll(domainEvent.PollId),
            filter);
    }

    public static IReadOnlyList<NotificationRequest> JoinRequestRejected(
        PollJoinRequestRejected domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            domainEvent.RequesterId,
            NotificationType.PollJoinRequestRejected,
            NotificationSubject.ForPoll(domainEvent.PollId),
            filter);
    }

    // ---- §10 friend-graph rows ------------------------------------------------------------------

    /// <summary>D-105: the addressee. Acceptance, removal and blocking are silent.</summary>
    public static IReadOnlyList<NotificationRequest> FriendRequest(
        FriendRequestReceived domainEvent,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return ExplicitTarget(
            domainEvent.AddresseeId,
            NotificationType.FriendRequestReceived,
            NotificationSubject.ForActor(domainEvent.RequestedBy),
            filter);
    }

    /// <summary>
    /// D-105/§12-32: the <em>importer</em> — at import time and again when an imported Steam friend
    /// registers or links later. The matched user learns nothing until an actual friend request
    /// arrives, because telling them would disclose the importer's Steam friend list.
    /// </summary>
    public static IReadOnlyList<NotificationRequest> SteamImportMatch(
        UserId importerId,
        UserId matchedUserId,
        RecipientFilter filter) =>
        ExplicitTarget(
            importerId,
            NotificationType.SteamImportMatch,
            NotificationSubject.ForActor(matchedUserId),
            filter);

    // ---- dispatchers ----------------------------------------------------------------------------

    /// <summary>
    /// Routes one drained session event to its §10 row. Events with no notification contract yield an
    /// empty set rather than throwing — draining is exhaustive, notifying is not.
    /// </summary>
    public static IReadOnlyList<NotificationRequest> ForSessionEvent(
        Session session,
        IDomainEvent domainEvent,
        RecipientFilter filter) => domainEvent switch
        {
            SessionInvitationSent e => Invitation(e, filter),
            SessionConfirmed e => Confirmed(session, e, filter),
            SessionUnconfirmed e => Unconfirmed(session, e, filter),
            SessionRescheduled e => Rescheduled(session, e, filter),
            SessionCanceled e => Canceled(session, e, filter),
            SessionJoinRequestReceived e => JoinRequestReceived(session, e, filter),
            SessionJoinRequestAccepted e => JoinRequestAccepted(e, filter),
            SessionJoinRequestRejected e => JoinRequestRejected(e, filter),
            SessionWaitlistAdded e => WaitlistAdded(e, filter),
            SessionWaitlistPromoted e => WaitlistPromoted(e, filter),
            SessionWaitlistRejected e => WaitlistRejected(e, filter),
            SessionWaitlistVacancy e => WaitlistVacancy(session, e, filter),
            _ => [],
        };

    public static IReadOnlyList<NotificationRequest> ForPollEvent(
        Poll poll,
        IDomainEvent domainEvent,
        RecipientFilter filter) => domainEvent switch
        {
            PollInvitationSent e => Invitation(e, filter),
            PollOptionSuggested e => OptionSuggested(poll, e, filter),
            PollClosed e => Closed(poll, e, filter),
            PollExpired e => Expired(poll, e, filter),
            PollCanceled e => Canceled(poll, e, filter),
            PollOptionSelected e => OptionSelected(poll, e, filter),
            PollWaitlistAdded e => WaitlistAdded(e, filter),
            PollJoinRequestReceived e => JoinRequestReceived(poll, e, filter),
            PollJoinRequestAccepted e => JoinRequestAccepted(e, filter),
            PollJoinRequestRejected e => JoinRequestRejected(e, filter),
            _ => [],
        };

    /// <summary>D-105: only the received request notifies; acceptance and blocking are silent.</summary>
    public static IReadOnlyList<NotificationRequest> ForFriendshipEvent(
        IDomainEvent domainEvent,
        RecipientFilter filter) => domainEvent switch
        {
            FriendRequestReceived e => FriendRequest(e, filter),
            _ => [],
        };

    // ---- internals ------------------------------------------------------------------------------

    private static IReadOnlyList<NotificationRequest> ExplicitTarget(
        UserId recipient,
        NotificationType type,
        NotificationSubject subject,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return filter.Allows(recipient, subject)
            ? [new NotificationRequest(recipient, type, subject)]
            : [];
    }

    /// <summary>D-167: a guest target simply produces nothing — guests have no notification channel.</summary>
    private static IReadOnlyList<NotificationRequest> ExplicitTarget(
        ParticipantRef participant,
        NotificationType type,
        NotificationSubject subject,
        RecipientFilter filter) =>
        participant.UserId is { } userId ? ExplicitTarget(userId, type, subject, filter) : [];

    private static List<NotificationRequest> Broadcast(
        IEnumerable<UserId> audience,
        NotificationType type,
        NotificationSubject subject,
        RecipientFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        List<NotificationRequest> requests = [];
        HashSet<UserId> seen = [];

        foreach (UserId recipient in audience)
        {
            if (seen.Add(recipient) && filter.Allows(recipient, subject))
            {
                requests.Add(new NotificationRequest(recipient, type, subject));
            }
        }

        return requests;
    }
}
