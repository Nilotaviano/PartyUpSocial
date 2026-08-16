using NodaTime;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Notifications;

/// <summary>
/// An in-app notification record (DOMAIN_MODEL §9, A-016), written in the same transaction as the
/// state change that caused it. Its payload is <em>structured refs only</em> — a type plus ids and
/// instants. There is deliberately no text, title or actor-name column: rendering happens at read
/// time in the recipient's locale through the masking service, which is what makes tombstoning and
/// blocking mask notification history retroactively (§11).
/// </summary>
/// <remarks>
/// Mute is applied at <em>write</em> time by <see cref="RecipientComputer"/>: a muted recipient gets
/// no row at all (D-090/D-113), the reminder being the sole exception (§9, <see cref="ReminderRules"/>).
/// </remarks>
public sealed class Notification
{
    private Notification()
    {
    }

    private Notification(
        NotificationId id,
        UserId recipientUserId,
        NotificationType type,
        NotificationSubject subject,
        Instant createdAt)
    {
        Id = id;
        RecipientUserId = recipientUserId;
        Type = type;
        SessionId = subject.SessionId;
        PollId = subject.PollId;
        ActorUserId = subject.ActorUserId;
        OptionStartTime = subject.OptionStartTime;
        FriendGroupId = subject.FriendGroupId;
        CreatedAt = createdAt;
    }

    public NotificationId Id { get; private set; }

    public UserId RecipientUserId { get; private set; }

    public NotificationType Type { get; private set; }

    public SessionId? SessionId { get; private set; }

    public PollId? PollId { get; private set; }

    public UserId? ActorUserId { get; private set; }

    public Instant? OptionStartTime { get; private set; }

    /// <summary>D-207: the group a friend-group row is about. Deliberately dangles once the group is hard-deleted.</summary>
    public FriendGroupId? FriendGroupId { get; private set; }

    public Instant CreatedAt { get; private set; }

    public Instant? ReadAt { get; private set; }

    public bool IsRead => ReadAt.HasValue;

    public NotificationSubject Subject =>
        NotificationSubject.Create(SessionId, PollId, ActorUserId, OptionStartTime, FriendGroupId);

    public static Notification Create(
        UserId recipientUserId,
        NotificationType type,
        NotificationSubject subject,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!Enum.IsDefined(type))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Unknown notification type '{type}'."));
        }

        if (type.NeedsSessionRef() && subject.SessionId is null)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"A '{type}' notification must carry the session it is about (§9)."));
        }

        if (type.NeedsPollRef() && subject.PollId is null)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"A '{type}' notification must carry the poll it is about (§9)."));
        }

        if (type.NeedsActorRef())
        {
            if (subject.ActorUserId is null)
            {
                throw new DomainException(
                    DomainError.ValidationFailed,
                    FormattableString.Invariant($"A '{type}' notification must carry the user it is about (D-105)."));
            }

            if (subject.SessionId is not null || subject.PollId is not null)
            {
                throw new DomainException(
                    DomainError.ValidationFailed,
                    FormattableString.Invariant($"A '{type}' notification names a person only (D-105)."));
            }
        }

        if (type.NeedsGroupRef() && subject.FriendGroupId is null)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"A '{type}' notification must carry the group it is about (D-207)."));
        }

        return new Notification(
            NotificationId.New(),
            recipientUserId,
            type,
            subject,
            clock.GetCurrentInstant());
    }

    /// <summary>Materializes what <see cref="RecipientComputer"/> computed for one recipient.</summary>
    public static Notification From(NotificationRequest request, IClock clock) =>
        Create(request.RecipientUserId, request.Type, request.Subject, clock);

    /// <summary>Idempotent: the first read wins, so a re-read never rewrites the timestamp.</summary>
    public void MarkRead(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        ReadAt ??= clock.GetCurrentInstant();
    }
}
