using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Polls;

/// <summary>
/// §12-11: a candidate instant is bounded to poll creation + one year — the D-130 ceiling's
/// analogue. Without it, finalization would mint sessions decades out that direct creation refuses.
/// There is deliberately no lower bound: options may precede the deadline (D-131) and elapsed
/// options stay selectable (D-140).
/// </summary>
public static class PollOptionWindow
{
    /// <summary>The same one-year lead the D-130 session window uses, measured from poll creation.</summary>
    public static Duration MaximumLead => SessionStartWindow.MaximumLead;

    public static bool IsWithin(Instant startTime, Instant pollCreatedAt) =>
        startTime - pollCreatedAt <= MaximumLead;

    public static void Require(Instant startTime, Instant pollCreatedAt)
    {
        if (!IsWithin(startTime, pollCreatedAt))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A poll option starts no later than {pollCreatedAt + MaximumLead}, but was {startTime} (§12-11)."));
        }
    }
}
