using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Sessions;

// The spec §8.4 session events (D-112), one record each — DOMAIN_MODEL §10. Domain events carry ids
// only: recipient computation, muting and rendering happen above, in the recipient's locale (§9).

/// <summary>Recipient: the new invitee only.</summary>
public sealed record SessionInvitationSent(SessionId SessionId, UserId InviteeId) : IDomainEvent;

/// <summary>Recipients: registered Pending/Going/Maybe/waitlisted participants + organizer (§12-31).</summary>
public sealed record SessionConfirmed(SessionId SessionId) : IDomainEvent;

/// <summary>Same audience as <see cref="SessionConfirmed"/>.</summary>
public sealed record SessionUnconfirmed(SessionId SessionId) : IDomainEvent;

/// <summary>Start-time (D-063) or duration (D-122) change; for a reset, recipients are computed after it.</summary>
public sealed record SessionRescheduled(SessionId SessionId) : IDomainEvent;

/// <summary>D-166: access-scoped, minus the organizer — the canceler is not a remaining participant (§12-31).</summary>
public sealed record SessionCanceled(SessionId SessionId, UserId OrganizerId) : IDomainEvent;

/// <summary>D-107: recipient is the organizer.</summary>
public sealed record SessionJoinRequestReceived(
    SessionId SessionId,
    JoinRequestId JoinRequestId,
    UserId RequesterId) : IDomainEvent;

/// <summary>D-107: the requester is an explicit target, not a visibility-set member.</summary>
public sealed record SessionJoinRequestAccepted(
    SessionId SessionId,
    JoinRequestId JoinRequestId,
    UserId RequesterId) : IDomainEvent;

/// <summary>D-107: reaches the requester even though approval never happened.</summary>
public sealed record SessionJoinRequestRejected(
    SessionId SessionId,
    JoinRequestId JoinRequestId,
    UserId RequesterId) : IDomainEvent;

/// <summary>D-084: the affected participant (guests are filtered out at recipient computation, D-167).</summary>
public sealed record SessionWaitlistAdded(SessionId SessionId, ParticipantRef Participant) : IDomainEvent;

public sealed record SessionWaitlistPromoted(SessionId SessionId, ParticipantRef Participant) : IDomainEvent;

/// <summary>Explicit target: it must reach the participant whose row this very command made Removed.</summary>
public sealed record SessionWaitlistRejected(SessionId SessionId, ParticipantRef Participant) : IDomainEvent;

/// <summary>Recipient: the organizer, who chooses whom to promote (spec §7.1-9).</summary>
public sealed record SessionWaitlistVacancy(SessionId SessionId) : IDomainEvent;
