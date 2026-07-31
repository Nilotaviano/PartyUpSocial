using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Sessions;

/// <summary>
/// D-130: a session starts at least fifteen minutes and at most one year in the future. The rule
/// applies to direct creation and to <em>every</em> reschedule, validated against the new start —
/// never to finalization-created sessions, whose option may already lie in the past (D-153).
/// </summary>
public static class SessionStartWindow
{
    /// <summary>One year expressed as 365 days: the domain rule is a lead time, not a calendar date.</summary>
    public static Duration MaximumLead => Duration.FromDays(365);

    public static Duration MinimumLead => Duration.FromMinutes(15);

    public static bool IsWithin(Instant startTime, Instant now)
    {
        Duration lead = startTime - now;
        return lead >= MinimumLead && lead <= MaximumLead;
    }

    public static void Require(Instant startTime, Instant now)
    {
        if (!IsWithin(startTime, now))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A session must start between {now + MinimumLead} and {now + MaximumLead}, but was {startTime} (D-130)."));
        }
    }
}
