using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Sessions;

/// <summary>
/// A registered user's request to join a session xor a poll (DOMAIN_MODEL §5.3 — one shape, one
/// table, a target CHECK). It carries no attendance intent (D-098). Lapse is the silent,
/// system-closed outcome: a block (§4), the target reaching a terminal state, a poll leaving
/// <c>Polling</c>, or the requester's deletion (§11).
/// </summary>
public sealed class JoinRequest
{
    private JoinRequest()
    {
    }

    private JoinRequest(JoinRequestId id, SessionId? sessionId, PollId? pollId, UserId requesterId, Instant createdAt)
    {
        Id = id;
        SessionId = sessionId;
        PollId = pollId;
        RequesterId = requesterId;
        Status = JoinRequestStatus.Pending;
        CreatedAt = createdAt;
    }

    public JoinRequestId Id { get; private set; }

    public SessionId? SessionId { get; private set; }

    public PollId? PollId { get; private set; }

    /// <summary>Registered requesters only — guests reach content through their link, never a request.</summary>
    public UserId RequesterId { get; private set; }

    public JoinRequestStatus Status { get; private set; }

    public Instant CreatedAt { get; private set; }

    public Instant? ResolvedAt { get; private set; }

    public bool IsPending => Status == JoinRequestStatus.Pending;

    public static JoinRequest ForSession(SessionId sessionId, UserId requesterId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new JoinRequest(JoinRequestId.New(), sessionId, null, requesterId, clock.GetCurrentInstant());
    }

    public static JoinRequest ForPoll(PollId pollId, UserId requesterId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new JoinRequest(JoinRequestId.New(), null, pollId, requesterId, clock.GetCurrentInstant());
    }

    /// <summary>D-098: approval grants Going (or Going-waitlisted); the target aggregate applies that.</summary>
    public void Approve(IClock clock) => Resolve(JoinRequestStatus.Approved, clock);

    public void Reject(IClock clock) => Resolve(JoinRequestStatus.Rejected, clock);

    /// <summary>D-107: a requester can withdraw a pending request.</summary>
    public void Withdraw(IClock clock) => Resolve(JoinRequestStatus.Withdrawn, clock);

    /// <summary>System-closed and silent; idempotent so a repeated sweep is a no-op.</summary>
    public void Lapse(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status == JoinRequestStatus.Lapsed)
        {
            return;
        }

        Resolve(JoinRequestStatus.Lapsed, clock);
    }

    private void Resolve(JoinRequestStatus status, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!IsPending)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"This join request is already {Status}."));
        }

        Status = status;
        ResolvedAt = clock.GetCurrentInstant();
    }
}
