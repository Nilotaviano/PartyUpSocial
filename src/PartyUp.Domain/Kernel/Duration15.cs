using NodaTime;

namespace PartyUp.Domain.Kernel;

/// <summary>A session or availability length: a multiple of 15 minutes, from 15 minutes to 24 hours (D-142, D-071).</summary>
public readonly record struct Duration15
{
    public const int GranularityMinutes = 15;
    public const int MinimumMinutes = 15;
    public const int MaximumMinutes = 24 * 60;

    private readonly int _minutes;

    private Duration15(int minutes) => _minutes = minutes;

    public static Duration Granularity => Duration.FromMinutes(GranularityMinutes);

    public static Duration MinimumValue => Duration.FromMinutes(MinimumMinutes);

    public static Duration MaximumValue => Duration.FromMinutes(MaximumMinutes);

    public int TotalMinutes => _minutes;

    public Duration Value => Duration.FromMinutes(_minutes);

    public static Duration15 FromMinutes(int minutes)
    {
        if (minutes is < MinimumMinutes or > MaximumMinutes)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"Duration must be between {MinimumMinutes} and {MaximumMinutes} minutes, but was {minutes}."));
        }

        if (minutes % GranularityMinutes != 0)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"Duration must be a multiple of {GranularityMinutes} minutes, but was {minutes}."));
        }

        return new Duration15(minutes);
    }

    public static Duration15 FromDuration(Duration duration)
    {
        if (duration < MinimumValue || duration > MaximumValue)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"Duration must be between {MinimumMinutes} and {MaximumMinutes} minutes, but was {duration}."));
        }

        long minutes = duration.BclCompatibleTicks / NodaConstants.TicksPerMinute;
        if (Duration.FromMinutes(minutes) != duration)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Duration must be a whole number of minutes, but was {duration}."));
        }

        return FromMinutes((int)minutes);
    }

    public static bool IsOnGrid(Duration duration)
    {
        long ticks = duration.BclCompatibleTicks;
        return Duration.FromTicks(ticks) == duration
            && ticks % (NodaConstants.TicksPerMinute * GranularityMinutes) == 0;
    }

    public static bool IsOnGrid(Instant instant) => IsOnGrid(instant - NodaConstants.UnixEpoch);

    public override string ToString() => FormattableString.Invariant($"{_minutes}m");
}
