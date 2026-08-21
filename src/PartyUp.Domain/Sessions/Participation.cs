using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Sessions;

/// <summary>
/// One roster row per (session, participant) — DOMAIN_MODEL §5.2. <c>Declined</c> is a response or a
/// voluntary departure; <c>Removed</c> is terminal and encodes expulsion (block D-077, waitlist
/// reject, guest removal D-044, deletion execution D-092) — it is never reset or re-invited over.
/// <see cref="IsWaitlisted"/> is orthogonal to the status so a waitlisted participant keeps the
/// Going/Maybe they selected (D-084).
/// </summary>
public sealed class Participation
{
    private Participation()
    {
    }

    private Participation(
        ParticipationId id,
        SessionId sessionId,
        ParticipantRef participant,
        ParticipationStatus status,
        ParticipationOrigin origin,
        bool isWaitlisted,
        Instant createdAt)
    {
        Id = id;
        SessionId = sessionId;
        UserId = participant.UserId;
        GuestIdentityId = participant.GuestIdentityId;
        Status = status;
        Origin = origin;
        IsWaitlisted = isWaitlisted;
        CreatedAt = createdAt;
        RespondedAt = status == ParticipationStatus.Pending ? null : createdAt;
    }

    public ParticipationId Id { get; private set; }

    public SessionId SessionId { get; private set; }

    public UserId? UserId { get; private set; }

    public GuestIdentityId? GuestIdentityId { get; private set; }

    public ParticipationStatus Status { get; private set; }

    /// <summary>Load-bearing for private-flip retention (§7 rule 1): an Invited row keeps access at any status.</summary>
    public ParticipationOrigin Origin { get; private set; }

    public bool IsWaitlisted { get; private set; }

    /// <summary>D-082: a per-(user, session) override of the account default; null means "use the default".</summary>
    public ReminderOffset? ReminderChoice { get; private set; }

    public Instant CreatedAt { get; private set; }

    public Instant? RespondedAt { get; private set; }

    public ParticipantRef Participant => ParticipantRef.Create(UserId, GuestIdentityId);

    public bool IsRegistered => UserId.HasValue;

    public bool IsGuest => GuestIdentityId.HasValue;

    public bool IsRemoved => Status == ParticipationStatus.Removed;

    /// <summary>Active Going and Maybe consume maximum capacity (D-084); waitlisted rows consume nothing.</summary>
    public bool IsActiveConsumer =>
        !IsWaitlisted && Status is ParticipationStatus.Going or ParticipationStatus.Maybe;

    /// <summary>Only active Going counts toward minimum attendance (D-062).</summary>
    public bool IsActiveGoing => !IsWaitlisted && Status == ParticipationStatus.Going;

    /// <summary>The D-074 grace candidate: a Going-waitlisted row preserves confirmation, a Maybe-waitlisted one does not.</summary>
    public bool IsGoingWaitlisted => IsWaitlisted && Status == ParticipationStatus.Going;

    public bool Matches(ParticipantRef participant) =>
        UserId == participant.UserId && GuestIdentityId == participant.GuestIdentityId;

    internal static Participation Organizer(SessionId sessionId, UserId organizerId, Instant now) =>
        new(
            ParticipationId.New(),
            sessionId,
            ParticipantRef.ForUser(organizerId),
            ParticipationStatus.Going,
            ParticipationOrigin.Invited,
            isWaitlisted: false,
            now);

    internal static Participation Invited(SessionId sessionId, ParticipantRef participant, Instant now) =>
        new(
            ParticipationId.New(),
            sessionId,
            participant,
            ParticipationStatus.Pending,
            ParticipationOrigin.Invited,
            isWaitlisted: false,
            now);

    internal static Participation FromJoinRequest(
        SessionId sessionId,
        ParticipantRef participant,
        bool isWaitlisted,
        Instant now) =>
        new(
            ParticipationId.New(),
            sessionId,
            participant,
            ParticipationStatus.Going,
            ParticipationOrigin.JoinRequest,
            isWaitlisted,
            now);

    internal static Participation FromGuestLink(
        SessionId sessionId,
        ParticipantRef participant,
        ParticipationStatus status,
        bool isWaitlisted,
        Instant now) =>
        new(ParticipationId.New(), sessionId, participant, status, ParticipationOrigin.GuestLink, isWaitlisted, now);

    internal static Participation FromPollFinalization(
        SessionId sessionId,
        ParticipantRef participant,
        ParticipationStatus status,
        bool isWaitlisted,
        Instant now) =>
        new(
            ParticipationId.New(),
            sessionId,
            participant,
            status,
            ParticipationOrigin.PollFinalization,
            isWaitlisted,
            now);

    internal void SetResponse(ParticipationStatus status, bool isWaitlisted, Instant now)
    {
        Status = status;
        IsWaitlisted = isWaitlisted;
        RespondedAt = now;
    }

    /// <summary>Re-inviting a Declined row re-extends visibility by restoring Origin = Invited (§5.6).</summary>
    internal void ReInvite()
    {
        Status = ParticipationStatus.Pending;
        Origin = ParticipationOrigin.Invited;
        IsWaitlisted = false;
        RespondedAt = null;
    }

    /// <summary>D-063: a start-time change returns the row to Pending and clears its waitlist flag.</summary>
    internal void ResetToPending()
    {
        Status = ParticipationStatus.Pending;
        IsWaitlisted = false;
        RespondedAt = null;
    }

    /// <summary>
    /// D-225: the guest row becomes the registered user's row in place — status, waitlist flag,
    /// <see cref="CreatedAt"/> and therefore the D-119 ordering are all untouched, so occupancy is
    /// unchanged and nothing is double-counted.
    /// </summary>
    internal void ReassignToUser(UserId userId)
    {
        if (!IsGuest)
        {
            throw new DomainException(DomainError.InvalidTransition, "Only a guest participation is reassigned to a user (D-225).");
        }

        UserId = userId;
        GuestIdentityId = null;
    }

    internal void Remove()
    {
        Status = ParticipationStatus.Removed;
        IsWaitlisted = false;
    }

    /// <summary>Promotion fills a free slot immediately and preserves the selected status (spec §8.1).</summary>
    internal void Activate() => IsWaitlisted = false;

    internal void SetReminderChoice(ReminderOffset? choice)
    {
        if (choice is { } offset && !Enum.IsDefined(offset))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Unknown reminder offset '{offset}'."));
        }

        if (!IsRegistered)
        {
            throw new DomainException(
                DomainError.Forbidden,
                "Only registered participants carry a reminder choice (D-082); guests receive no notifications (D-167).");
        }

        ReminderChoice = choice;
    }
}
