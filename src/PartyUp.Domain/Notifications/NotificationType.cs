namespace PartyUp.Domain.Notifications;

/// <summary>
/// One member per spec §8.4 event row (DOMAIN_MODEL §10, D-112). The type plus the structured
/// subject refs are the <em>whole</em> payload: rendering happens at read time in the recipient's
/// locale, through the masking service (§9).
/// </summary>
// Values are persisted: append only, never renumber.
public enum NotificationType
{
    SessionInvitation = 0,
    SessionConfirmed = 1,
    SessionUnconfirmed = 2,
    SessionRescheduled = 3,
    SessionCanceled = 4,
    SessionJoinRequestReceived = 5,
    SessionJoinRequestAccepted = 6,
    SessionJoinRequestRejected = 7,
    WaitlistAdded = 8,
    WaitlistPromoted = 9,
    WaitlistRejected = 10,
    WaitlistVacancy = 11,
    SessionReminder = 12,
    PollInvitation = 13,
    PollOptionSuggested = 14,
    PollDeadlineApproaching = 15,
    PollClosed = 16,
    PollExpired = 17,
    PollCanceled = 18,
    PollOptionSelected = 19,
    PollJoinRequestReceived = 20,
    PollJoinRequestAccepted = 21,
    PollJoinRequestRejected = 22,
    FriendRequestReceived = 23,
    SteamImportMatch = 24,
    FriendGroupInviteReceived = 25,
    FriendGroupInviteAccepted = 26,
    AudienceGroupDeleted = 27,
}

/// <summary>
/// Which structured refs each type must carry (§9). These are the shape rules a
/// <see cref="Notification"/> is validated against — there is no free-text fallback for a missing
/// reference, because a notification whose subject is gone still renders from its own fields.
/// </summary>
public static class NotificationTypeExtensions
{
    /// <summary>
    /// A session ref is required by every session row and by "option selected", which names the
    /// session finalization minted (§6.4-8).
    /// </summary>
    public static bool NeedsSessionRef(this NotificationType type) => type switch
    {
        NotificationType.SessionInvitation
            or NotificationType.SessionConfirmed
            or NotificationType.SessionUnconfirmed
            or NotificationType.SessionRescheduled
            or NotificationType.SessionCanceled
            or NotificationType.SessionJoinRequestReceived
            or NotificationType.SessionJoinRequestAccepted
            or NotificationType.SessionJoinRequestRejected
            or NotificationType.WaitlistAdded
            or NotificationType.WaitlistPromoted
            or NotificationType.WaitlistRejected
            or NotificationType.WaitlistVacancy
            or NotificationType.SessionReminder
            or NotificationType.PollOptionSelected => true,
        _ => false,
    };

    public static bool NeedsPollRef(this NotificationType type) => type switch
    {
        NotificationType.PollInvitation
            or NotificationType.PollOptionSuggested
            or NotificationType.PollDeadlineApproaching
            or NotificationType.PollClosed
            or NotificationType.PollExpired
            or NotificationType.PollCanceled
            or NotificationType.PollOptionSelected
            or NotificationType.PollJoinRequestReceived
            or NotificationType.PollJoinRequestAccepted
            or NotificationType.PollJoinRequestRejected => true,
        _ => false,
    };

    /// <summary>
    /// The friend-graph rows (D-105) name a person and nothing else: the requester, or the matched
    /// Steam friend (§12-32). Carrying a session or poll ref on one of them is a shape error. The two
    /// friend-group membership rows (D-207) follow the identical shape: the counterpart in the invite
    /// (the owner who sent it, or the invitee who accepted it) and nothing else — <c>AudienceGroupDeleted</c>
    /// carries no ref at all, since the group that would be named is the very thing that is gone.
    /// </summary>
    public static bool NeedsActorRef(this NotificationType type) =>
        type is NotificationType.FriendRequestReceived
            or NotificationType.SteamImportMatch
            or NotificationType.FriendGroupInviteReceived
            or NotificationType.FriendGroupInviteAccepted;
}
