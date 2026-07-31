using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Sessions;

/// <summary>
/// One row of the roster poll finalization hands to <see cref="Session.FromPollFinalization"/>:
/// the vote mapping of §6.4-3 (Available → Going, Maybe → Maybe, Unavailable → Declined, no vote →
/// Pending, organizer → Going regardless) already resolved, plus the D-119 overflow flag.
/// </summary>
public readonly record struct FinalizedParticipation(
    ParticipantRef Participant,
    ParticipationStatus Status,
    bool IsWaitlisted);
