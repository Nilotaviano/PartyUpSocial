using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>
/// The recurring shape of a weekly availability series (D-025/D-132): which ISO weekday, at which
/// local wall-clock time, for how long. Length carries <see cref="Duration15"/> semantics (D-142).
/// </summary>
public readonly record struct WeeklyPattern
{
    private WeeklyPattern(IsoDayOfWeek dayOfWeek, LocalTime startTime, Duration15 length)
    {
        DayOfWeek = dayOfWeek;
        StartTime = startTime;
        Length = length;
    }

    public IsoDayOfWeek DayOfWeek { get; }

    public LocalTime StartTime { get; }

    public Duration15 Length { get; }

    public static WeeklyPattern Create(IsoDayOfWeek dayOfWeek, LocalTime startTime, Duration15 length)
    {
        if (dayOfWeek is < IsoDayOfWeek.Monday or > IsoDayOfWeek.Sunday)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"'{dayOfWeek}' is not an ISO day of the week."));
        }

        if (startTime.Second != 0
            || startTime.NanosecondOfSecond != 0
            || startTime.Minute % Duration15.GranularityMinutes != 0)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"Availability must start on the {Duration15.GranularityMinutes}-minute grid, but was {startTime}."));
        }

        return new WeeklyPattern(dayOfWeek, startTime, length);
    }

    /// <summary>
    /// A minutes-only period so occurrence expansion is LocalDateTime arithmetic: a 23:00 + 2 h block
    /// lands at 01:00 the following day rather than wrapping to 01:00 the same day (§12-19).
    /// </summary>
    public Period ToPeriod() => Period.FromMinutes(Length.TotalMinutes);

    public override string ToString() =>
        FormattableString.Invariant($"{DayOfWeek} {StartTime:HH:mm} +{Length}");
}
