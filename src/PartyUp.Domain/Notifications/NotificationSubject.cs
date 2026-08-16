using NodaTime;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Notifications;

/// <summary>
/// The structured subject of a notification (§9): ids and instants only — never a pre-rendered
/// string and never a denormalized name, so tombstoning and blocking retroactively mask history and
/// no PII survives in notification rows (§11).
/// </summary>
public readonly record struct NotificationSubject
{
    private NotificationSubject(
        SessionId? sessionId,
        PollId? pollId,
        UserId? actorUserId,
        Instant? optionStartTime,
        FriendGroupId? friendGroupId)
    {
        SessionId = sessionId;
        PollId = pollId;
        ActorUserId = actorUserId;
        OptionStartTime = optionStartTime;
        FriendGroupId = friendGroupId;
    }

    public SessionId? SessionId { get; }

    public PollId? PollId { get; }

    /// <summary>The person the row is <em>about</em> — a friend requester, a matched Steam friend, the canceling organizer.</summary>
    public UserId? ActorUserId { get; }

    /// <summary>A candidate time (§9): the instant a voter suggested, or the one finalization selected.</summary>
    public Instant? OptionStartTime { get; }

    /// <summary>
    /// D-207: the group a friend-group row is about. Never denormalized to a name — the read side
    /// resolves the live <c>FriendGroup</c> only when the viewer still <see cref="FriendGroup.Involves"/>
    /// it, which is what lets a hard-deleted group (the ref permanently dangling) and a revoked viewer
    /// both collapse to the same masked fallback instead of leaking stale state.
    /// </summary>
    public FriendGroupId? FriendGroupId { get; }

    public static NotificationSubject Create(
        SessionId? sessionId,
        PollId? pollId,
        UserId? actorUserId = null,
        Instant? optionStartTime = null,
        FriendGroupId? friendGroupId = null) =>
        new(sessionId, pollId, actorUserId, optionStartTime, friendGroupId);

    public static NotificationSubject ForSession(SessionId sessionId, UserId? actorUserId = null) =>
        new(sessionId, null, actorUserId, null, null);

    public static NotificationSubject ForPoll(PollId pollId, UserId? actorUserId = null) =>
        new(null, pollId, actorUserId, null, null);

    /// <summary>A suggested option (D-120): the organizer's row renders the instant, not a rendered time string.</summary>
    public static NotificationSubject ForPollOption(
        PollId pollId,
        Instant? optionStartTime,
        UserId? actorUserId = null) =>
        new(null, pollId, actorUserId, optionStartTime, null);

    /// <summary>§6.4-8: "option selected" names the poll, the session it minted, and the chosen instant.</summary>
    public static NotificationSubject ForSelectedOption(
        PollId pollId,
        SessionId sessionId,
        Instant? optionStartTime) =>
        new(sessionId, pollId, null, optionStartTime, null);

    /// <summary>The friend-graph rows (D-105): a person and nothing else.</summary>
    public static NotificationSubject ForActor(UserId actorUserId) => new(null, null, actorUserId, null, null);

    /// <summary>D-207: the friend-group invite rows — the counterpart plus the group the invite is about.</summary>
    public static NotificationSubject ForFriendGroupInvite(FriendGroupId groupId, UserId actorUserId) =>
        new(null, null, actorUserId, null, groupId);

    /// <summary>D-205/D-207: the group an organizer's audience lost, named only while it still exists.</summary>
    public static NotificationSubject ForFriendGroupDeleted(FriendGroupId groupId) =>
        new(null, null, null, null, groupId);
}
