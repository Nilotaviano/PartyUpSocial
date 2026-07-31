using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Availability;

/// <summary>
/// The overlap engine's prefill consumers (DOMAIN_MODEL §8).
/// <para>
/// D-102: a recommended window prefills a fixed session or a poll option. A window may start in the
/// past or nearer than the D-130 minimum lead — the recommendation range begins at <c>now</c> and a
/// window is only produced from availability that may already have begun — so the prefilled start is
/// clamped <em>up</em> to <c>now + 15 min</c>, never moved earlier and never widened. The clamped
/// start therefore always satisfies <see cref="SessionStartWindow"/>; only the one-year ceiling,
/// which no clamp can reach down to, still rejects.
/// </para>
/// <para>
/// D-109: the chosen duration is never clamped, shortened, or rejected. A session that outlasts its
/// recommended window is accepted and carries an over-duration warning measured from the clamped
/// start, so a window half-consumed by the clamp reports the overshoot the organizer will actually
/// experience.
/// </para>
/// </summary>
public static class RecommendationPrefill
{
    /// <summary>D-130's minimum lead — the floor a prefilled start is raised to.</summary>
    public static Duration MinimumLead => SessionStartWindow.MinimumLead;

    /// <summary>
    /// The earliest start a prefill may propose: <c>now + 15 min</c> (D-102 → D-130).
    /// </summary>
    public static Instant EarliestStart(Instant now) => now + MinimumLead;

    /// <summary>Raises <paramref name="windowStart"/> to <see cref="EarliestStart"/>; never lowers it.</summary>
    public static Instant ClampStart(Instant windowStart, Instant now) =>
        Instant.Max(windowStart, EarliestStart(now));

    public static bool WouldClamp(Instant windowStart, Instant now) => windowStart < EarliestStart(now);

    /// <summary>
    /// D-109: how far past <paramref name="window"/> a session of <paramref name="duration"/> starting
    /// at <paramref name="startTime"/> runs. Zero when it fits — half-open, so ending exactly at the
    /// window's end fits (DOMAIN_MODEL §1).
    /// </summary>
    public static Duration OverDuration(Instant startTime, Duration15 duration, TimeRange window)
    {
        Instant end = startTime + duration.Value;
        return end > window.End ? end - window.End : Duration.Zero;
    }

    public static SessionPrefill ForSession(TimeRange window, Duration15 duration, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();
        Instant startTime = ClampStart(window.Start, now);
        SessionStartWindow.Require(startTime, now);

        return new SessionPrefill(
            startTime,
            duration,
            WouldClamp(window.Start, now),
            OverDuration(startTime, duration, window));
    }

    public static SessionPrefill ForSession(OverlapWindow window, Duration15 duration, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(window);
        return ForSession(window.Range, duration, clock);
    }

    public static PollOptionPrefill ForPollOption(TimeRange window, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();
        Instant startTime = ClampStart(window.Start, now);
        PollOptionWindow.Require(startTime, now);

        return new PollOptionPrefill(startTime, WouldClamp(window.Start, now));
    }

    public static PollOptionPrefill ForPollOption(OverlapWindow window, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(window);
        return ForPollOption(window.Range, clock);
    }
}
