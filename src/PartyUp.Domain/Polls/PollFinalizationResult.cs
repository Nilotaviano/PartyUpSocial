using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Polls;

/// <summary>
/// §12-15: the poll's <c>NotificationMute</c> rows carry onto the session finalization created — the
/// user muted the plan, not the artifact. The mute rows themselves belong to the Notifications
/// stage; this pair is the hook it copies along.
/// </summary>
public readonly record struct MuteCarryOver(PollId FromPollId, SessionId ToSessionId);

/// <summary>
/// What <see cref="Poll.Finalize"/> hands back (DOMAIN_MODEL §6.4): the created session plus the
/// facts the application layer needs to finish the transaction — whom the D-119 overflow waitlisted
/// and which mutes to carry over. Reminders (§6.4-9) are scheduled from the session's roster.
/// </summary>
public sealed record PollFinalizationResult(
    Session Session,
    PollOptionId SelectedOptionId,
    Instant SelectedStartTime,
    IReadOnlyList<ParticipantRef> Waitlisted,
    MuteCarryOver MuteCarryOver);
