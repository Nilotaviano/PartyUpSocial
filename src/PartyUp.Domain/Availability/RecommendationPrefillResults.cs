using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>
/// A fixed session prefilled from a recommended window (D-102). <see cref="OverDuration"/> carries
/// the D-109 warning: it is reported, never enforced — the duration in <see cref="ChosenDuration"/>
/// is exactly the one asked for.
/// </summary>
public sealed record SessionPrefill(
    Instant StartTime,
    Duration15 ChosenDuration,
    bool StartWasClamped,
    Duration OverDuration)
{
    public bool HasOverDurationWarning => OverDuration > Duration.Zero;

    public TimeRange Range => TimeRange.FromDuration(StartTime, ChosenDuration.Value);
}

/// <summary>A poll candidate instant prefilled from a recommended window (D-102).</summary>
public sealed record PollOptionPrefill(Instant StartTime, bool StartWasClamped);
