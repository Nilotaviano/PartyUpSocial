using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Tests.Availability;

internal static class AvailabilityTestData
{
    internal const int BaseYear = 2026;

    internal static DateTimeZone Santiago => DateTimeZoneProviders.Tzdb["America/Santiago"];

    internal static Instant At(int year, int month, int day, int hour, int minute) =>
        Instant.FromUtc(year, month, day, hour, minute);

    /// <summary>A same-day UTC range on 2026-08-03 (a Monday), given whole hours.</summary>
    internal static TimeRange Hours(int startHour, int endHour) =>
        new(At(BaseYear, 8, 3, startHour, 0), At(BaseYear, 8, 3, endHour, 0));

    internal static TimeRange Minutes(int startHour, int startMinute, int endHour, int endMinute) =>
        new(At(BaseYear, 8, 3, startHour, startMinute), At(BaseYear, 8, 3, endHour, endMinute));

    internal static TimeRange WholeDay =>
        new(At(BaseYear, 8, 3, 0, 0), At(BaseYear, 8, 4, 0, 0));

    internal static AvailabilitySeries WeeklySeries(
        UserId owner,
        IsoDayOfWeek dayOfWeek,
        LocalTime startTime,
        int lengthMinutes,
        DateTimeZone zone,
        LocalDate effectiveFrom) =>
        AvailabilitySeries.Create(
            owner,
            WeeklyPattern.Create(dayOfWeek, startTime, Duration15.FromMinutes(lengthMinutes)),
            zone,
            effectiveFrom);

    internal static FriendAvailability Friend(UserId id, params TimeRange[] blocks) =>
        FriendAvailability.Create(id, blocks);
}
