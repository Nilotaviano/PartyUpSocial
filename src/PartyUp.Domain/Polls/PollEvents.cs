using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Polls;

// The spec §8.4 poll events (D-112), one record each — DOMAIN_MODEL §10. Domain events carry ids
// only: recipient computation, muting and rendering happen above, in the recipient's locale (§9).
// "Deadline approaching" (D-120) is a scheduled event rather than a transition and is not raised here.

/// <summary>Recipient: the new invitee only.</summary>
public sealed record PollInvitationSent(PollId PollId, UserId InviteeId) : IDomainEvent;

/// <summary>D-120: the organizer only — voters are not told about each other's suggestions.</summary>
public sealed record PollOptionSuggested(
    PollId PollId,
    PollOptionId OptionId,
    ParticipantRef SuggestedBy) : IDomainEvent;

/// <summary>D-079/D-120: closed awaiting selection — all Active registered participants + organizer.</summary>
public sealed record PollClosed(PollId PollId) : IDomainEvent;

/// <summary>D-170: expiry without a selection notifies every registered poll participant.</summary>
public sealed record PollExpired(PollId PollId) : IDomainEvent;

/// <summary>D-166: cancellation notifies every registered poll participant, minus the organizer (§12-31).</summary>
public sealed record PollCanceled(PollId PollId, UserId OrganizerId) : IDomainEvent;

/// <summary>
/// §6.4-8/§12-16: raised in <em>every</em> finalization branch, the Expired one included — the spec
/// §8.4 list has no branch exception and voters must learn the outcome.
/// </summary>
public sealed record PollOptionSelected(PollId PollId, PollOptionId OptionId, SessionId SessionId) : IDomainEvent;

/// <summary>
/// §6.4-8: "added to waitlist" for each participant the D-119 overflow waitlisted. It is a poll
/// event because the session it names was minted with its roster already allocated.
/// </summary>
public sealed record PollWaitlistAdded(
    PollId PollId,
    SessionId SessionId,
    ParticipantRef Participant) : IDomainEvent;

/// <summary>D-107: recipient is the organizer.</summary>
public sealed record PollJoinRequestReceived(
    PollId PollId,
    JoinRequestId JoinRequestId,
    UserId RequesterId) : IDomainEvent;

/// <summary>D-107: the requester is an explicit target, not a visibility-set member.</summary>
public sealed record PollJoinRequestAccepted(
    PollId PollId,
    JoinRequestId JoinRequestId,
    UserId RequesterId) : IDomainEvent;

/// <summary>D-107: reaches the requester even though approval never happened.</summary>
public sealed record PollJoinRequestRejected(
    PollId PollId,
    JoinRequestId JoinRequestId,
    UserId RequesterId) : IDomainEvent;
