using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Polls;

/// <summary>
/// What selecting one option would produce right now (DOMAIN_MODEL §6.4): the mapped roster in
/// D-119 allocation order with the overflow already split off, and whether the option's Going count
/// reaches <see cref="Poll.MinAttendance"/> — the D-153 arithmetic that also decides whether D-187
/// lets the organizer select before the deadline.
/// </summary>
public sealed record PollOptionProjection(
    PollOptionId OptionId,
    Instant StartTime,
    bool MeetsMinimum,
    IReadOnlyList<FinalizedParticipation> Roster,
    IReadOnlyList<ParticipantRef> Waitlisted);
