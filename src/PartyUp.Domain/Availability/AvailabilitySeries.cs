using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>
/// Weekly recurring availability (D-025/D-132/D-133). <see cref="EffectiveFrom"/> is the dragged
/// week's date — there is no infinite-past expansion — and a null <see cref="EffectiveUntil"/>
/// continues until the series is deleted. <see cref="EffectiveUntil"/> is exclusive.
/// </summary>
public sealed class AvailabilitySeries : AggregateRoot
{
    private AvailabilitySeries()
    {
        ZoneId = string.Empty;
        Duration = Period.Zero;
    }

    private AvailabilitySeries(
        AvailabilitySeriesId id,
        UserId ownerId,
        WeeklyPattern pattern,
        string zoneId,
        LocalDate effectiveFrom,
        LocalDate? effectiveUntil)
    {
        EnsureEffectiveRange(effectiveFrom, effectiveUntil);

        Id = id;
        OwnerId = ownerId;
        DayOfWeek = pattern.DayOfWeek;
        StartTime = pattern.StartTime;
        Duration = pattern.ToPeriod();
        ZoneId = zoneId;
        EffectiveFrom = effectiveFrom;
        EffectiveUntil = effectiveUntil;
    }

    public AvailabilitySeriesId Id { get; private set; }

    public UserId OwnerId { get; private set; }

    public IsoDayOfWeek DayOfWeek { get; private set; }

    public LocalTime StartTime { get; private set; }

    /// <summary>Minutes-only, so occurrence expansion never wraps across midnight (§12-19).</summary>
    public Period Duration { get; private set; }

    /// <summary>
    /// The tzdb id, not a resolved zone: a series whose id has left the tzdb still has to expand,
    /// falling back to the owner's current zone (§12-29).
    /// </summary>
    public string ZoneId { get; private set; }

    public LocalDate EffectiveFrom { get; private set; }

    public LocalDate? EffectiveUntil { get; private set; }

    public Duration15 Length => Duration15.FromDuration(Duration.ToDuration());

    public WeeklyPattern Pattern => WeeklyPattern.Create(DayOfWeek, StartTime, Length);

    /// <summary>True once a split has clipped the series down to nothing (§12-20).</summary>
    public bool IsEmpty => EffectiveUntil == EffectiveFrom;

    public static AvailabilitySeries Create(
        UserId ownerId,
        WeeklyPattern pattern,
        DateTimeZone zone,
        LocalDate effectiveFrom) =>
        Create(ownerId, pattern, zone, effectiveFrom, null);

    public static AvailabilitySeries Create(
        UserId ownerId,
        WeeklyPattern pattern,
        DateTimeZone zone,
        LocalDate effectiveFrom,
        LocalDate? effectiveUntil)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (effectiveUntil is { } until && until <= effectiveFrom)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A weekly series must end after it starts, but was [{effectiveFrom}, {until})."));
        }

        return new AvailabilitySeries(
            AvailabilitySeriesId.New(), ownerId, pattern, zone.Id, effectiveFrom, effectiveUntil);
    }

    /// <summary>The successor half of an edit-this-and-future split: same owner, same zone (D-143).</summary>
    public AvailabilitySeries Continue(WeeklyPattern pattern, LocalDate effectiveFrom, LocalDate? effectiveUntil) =>
        new(AvailabilitySeriesId.New(), OwnerId, pattern, ZoneId, effectiveFrom, effectiveUntil);

    /// <summary>D-061: the same local clock time in the new zone, so only the zone moves.</summary>
    public void ChangeZone(DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ZoneId = zone.Id;
    }

    /// <summary>Clips the series so occurrences on or after <paramref name="effectiveUntil"/> stop.</summary>
    public void TruncateAt(LocalDate effectiveUntil)
    {
        if (effectiveUntil < EffectiveFrom)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A weekly series cannot end before it starts, but was [{EffectiveFrom}, {effectiveUntil})."));
        }

        if (EffectiveUntil is { } existing && effectiveUntil > existing)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A weekly series ending {existing} cannot be extended to {effectiveUntil}."));
        }

        EffectiveUntil = effectiveUntil;
    }

    public bool CoversDate(LocalDate date) =>
        date >= EffectiveFrom && (EffectiveUntil is not { } until || date < until);

    public bool IsOccurrenceDate(LocalDate date) => date.DayOfWeek == DayOfWeek && CoversDate(date);

    public void EnsureOccurrenceDate(LocalDate date)
    {
        if (!IsOccurrenceDate(date))
        {
            throw new DomainException(
                DomainError.NotFound,
                FormattableString.Invariant($"{date} is not an occurrence of this weekly availability series."));
        }
    }

    private static void EnsureEffectiveRange(LocalDate effectiveFrom, LocalDate? effectiveUntil)
    {
        if (effectiveUntil is { } until && until < effectiveFrom)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A weekly series cannot end before it starts, but was [{effectiveFrom}, {until})."));
        }
    }
}
