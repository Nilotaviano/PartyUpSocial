using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Sessions;

/// <summary>
/// The scheduling payload a session is built from (DOMAIN_MODEL §5.1) and the shape a poll carries
/// as "every eventual session detail" (§6.1). Every field is required: the D-129 creation defaults
/// (minimum 4, maximum 4, two hours, platform N/A, non-private) are application-layer, so nothing
/// here may silently substitute one.
/// </summary>
public sealed record SessionDetails
{
    public SessionDetails(
        SessionTitle title,
        Instant startTime,
        Duration15 duration,
        Platform platform,
        SessionGame? game,
        SessionNotes notes,
        int minAttendance,
        int? maxCapacity,
        bool isPrivate)
    {
        if (title.Value.Length < SessionTitle.MinLength)
        {
            throw new DomainException(DomainError.ValidationFailed, "A session requires a title (D-057).");
        }

        if (duration.TotalMinutes < Duration15.MinimumMinutes)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A session lasts {Duration15.MinimumMinutes} minutes to {Duration15.MaximumMinutes} minutes (D-142)."));
        }

        if (!Enum.IsDefined(platform))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Unknown platform '{platform}'."));
        }

        game?.EnsureAllowedOn(platform);

        if (minAttendance < 1)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Minimum attendance is an integer of at least 1, but was {minAttendance} (D-141)."));
        }

        if (maxCapacity is { } max && max < minAttendance)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"Maximum capacity {max} cannot be below the minimum attendance {minAttendance} (D-141)."));
        }

        Title = title;
        StartTime = startTime;
        Duration = duration;
        Platform = platform;
        Game = game;
        Notes = notes;
        MinAttendance = minAttendance;
        MaxCapacity = maxCapacity;
        IsPrivate = isPrivate;
    }

    public SessionTitle Title { get; }

    public Instant StartTime { get; }

    public Duration15 Duration { get; }

    public Platform Platform { get; }

    /// <summary>§5.1: Steam catalog reference xor free text; every other platform is free text only.</summary>
    public SessionGame? Game { get; }

    public SessionNotes Notes { get; }

    /// <summary>D-141: an integer of at least 1; only active Going rows count toward it.</summary>
    public int MinAttendance { get; }

    /// <summary>D-091: null means unlimited attendance and therefore no waitlist can exist.</summary>
    public int? MaxCapacity { get; }

    public bool IsPrivate { get; }

    /// <summary>The half-open span the session occupies; <c>EndTime</c> is derived, never stored (§1).</summary>
    public TimeRange Window => TimeRange.FromDuration(StartTime, Duration.Value);
}
