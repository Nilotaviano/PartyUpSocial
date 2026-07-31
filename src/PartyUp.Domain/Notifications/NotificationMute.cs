using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Notifications;

/// <summary>
/// A user's mute on one session xor one poll (DOMAIN_MODEL §2/§9, D-090/D-113). It suppresses the
/// notification <em>record</em>, not merely its push: recipient computation drops the muted user at
/// write time, so nothing is ever backfilled by unmuting. The only exception is the reminder, which
/// is governed by <c>ReminderChoice</c>/<c>ReminderDefault</c> instead (§9, <see cref="ReminderRules"/>).
/// </summary>
public sealed class NotificationMute
{
    private NotificationMute()
    {
    }

    private NotificationMute(
        NotificationMuteId id,
        UserId userId,
        SessionId? sessionId,
        PollId? pollId,
        Instant createdAt)
    {
        Id = id;
        UserId = userId;
        SessionId = sessionId;
        PollId = pollId;
        CreatedAt = createdAt;
    }

    public NotificationMuteId Id { get; private set; }

    public UserId UserId { get; private set; }

    public SessionId? SessionId { get; private set; }

    public PollId? PollId { get; private set; }

    public Instant CreatedAt { get; private set; }

    public bool TargetsSession => SessionId.HasValue;

    public bool TargetsPoll => PollId.HasValue;

    public static NotificationMute ForSession(UserId userId, SessionId sessionId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new NotificationMute(
            NotificationMuteId.New(),
            userId,
            sessionId,
            null,
            clock.GetCurrentInstant());
    }

    public static NotificationMute ForPoll(UserId userId, PollId pollId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new NotificationMute(
            NotificationMuteId.New(),
            userId,
            null,
            pollId,
            clock.GetCurrentInstant());
    }

    public bool Targets(SessionId sessionId) => SessionId == sessionId;

    public bool Targets(PollId pollId) => PollId == pollId;

    /// <summary>
    /// §6.4-6/§12-15: finalization carries a poll's mutes onto the session it minted — the user muted
    /// the plan, not the artifact. Like guest links, the row re-parents rather than being re-created.
    /// </summary>
    public void AttachToSession(SessionId sessionId)
    {
        if (PollId is null)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                "Only a poll mute can be re-parented onto a session (§12-15).");
        }

        SessionId = sessionId;
        PollId = null;
    }
}
