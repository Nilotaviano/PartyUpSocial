using NodaTime;
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
        Instant? optionStartTime)
    {
        SessionId = sessionId;
        PollId = pollId;
        ActorUserId = actorUserId;
        OptionStartTime = optionStartTime;
    }

    public SessionId? SessionId { get; }

    public PollId? PollId { get; }

    /// <summary>The person the row is <em>about</em> — a friend requester, a matched Steam friend, the canceling organizer.</summary>
    public UserId? ActorUserId { get; }

    /// <summary>A candidate time (§9): the instant a voter suggested, or the one finalization selected.</summary>
    public Instant? OptionStartTime { get; }

    public static NotificationSubject Create(
        SessionId? sessionId,
        PollId? pollId,
        UserId? actorUserId = null,
        Instant? optionStartTime = null) =>
        new(sessionId, pollId, actorUserId, optionStartTime);

    public static NotificationSubject ForSession(SessionId sessionId, UserId? actorUserId = null) =>
        new(sessionId, null, actorUserId, null);

    public static NotificationSubject ForPoll(PollId pollId, UserId? actorUserId = null) =>
        new(null, pollId, actorUserId, null);

    /// <summary>A suggested option (D-120): the organizer's row renders the instant, not a rendered time string.</summary>
    public static NotificationSubject ForPollOption(
        PollId pollId,
        Instant? optionStartTime,
        UserId? actorUserId = null) =>
        new(null, pollId, actorUserId, optionStartTime);

    /// <summary>§6.4-8: "option selected" names the poll, the session it minted, and the chosen instant.</summary>
    public static NotificationSubject ForSelectedOption(
        PollId pollId,
        SessionId sessionId,
        Instant? optionStartTime) =>
        new(sessionId, pollId, null, optionStartTime);

    /// <summary>The friend-graph rows (D-105): a person and nothing else.</summary>
    public static NotificationSubject ForActor(UserId actorUserId) => new(null, null, actorUserId, null);
}
