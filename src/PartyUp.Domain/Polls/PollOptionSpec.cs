using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Polls;

/// <summary>
/// One candidate option as creation supplies it (D-211): its own start instant and its own length.
/// The length obeys D-142 per option, and the option's end stays derived from the pair (§1).
/// </summary>
public readonly record struct PollOptionSpec(Instant StartTime, Duration15 Duration);
