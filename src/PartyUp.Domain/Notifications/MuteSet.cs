using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Notifications;

/// <summary>
/// The mute rows relevant to one emission, as a pure lookup. Recipient computation consults it at
/// write time (§9): a muted recipient produces no <see cref="NotificationRequest"/> at all.
/// </summary>
public sealed class MuteSet
{
    /// <summary>Nobody has muted anything — the common case, and the default filter's set.</summary>
    public static readonly MuteSet Empty = new([], []);

    private readonly HashSet<(UserId User, SessionId Session)> _sessionMutes;
    private readonly HashSet<(UserId User, PollId Poll)> _pollMutes;

    private MuteSet(
        HashSet<(UserId User, SessionId Session)> sessionMutes,
        HashSet<(UserId User, PollId Poll)> pollMutes)
    {
        _sessionMutes = sessionMutes;
        _pollMutes = pollMutes;
    }

    public int Count => _sessionMutes.Count + _pollMutes.Count;

    public static MuteSet Create(IEnumerable<NotificationMute> mutes)
    {
        ArgumentNullException.ThrowIfNull(mutes);

        HashSet<(UserId, SessionId)> sessions = [];
        HashSet<(UserId, PollId)> polls = [];

        foreach (NotificationMute mute in mutes)
        {
            ArgumentNullException.ThrowIfNull(mute);

            if (mute.SessionId is { } sessionId)
            {
                sessions.Add((mute.UserId, sessionId));
            }

            if (mute.PollId is { } pollId)
            {
                polls.Add((mute.UserId, pollId));
            }
        }

        return new MuteSet(sessions, polls);
    }

    public bool IsMuted(UserId user, SessionId sessionId) => _sessionMutes.Contains((user, sessionId));

    public bool IsMuted(UserId user, PollId pollId) => _pollMutes.Contains((user, pollId));

    /// <summary>
    /// A mute on <em>either</em> target a notification references suppresses it. That matters for the
    /// finalization rows, which name the poll and the session it minted at once (§12-15).
    /// </summary>
    public bool IsMuted(UserId user, SessionId? sessionId, PollId? pollId) =>
        (sessionId is { } session && IsMuted(user, session))
        || (pollId is { } poll && IsMuted(user, poll));
}
