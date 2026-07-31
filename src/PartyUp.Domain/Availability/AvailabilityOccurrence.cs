using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>One expanded occurrence of a weekly series, resolved to absolute instants.</summary>
public readonly record struct AvailabilityOccurrence(
    AvailabilitySeriesId SeriesId,
    LocalDate OccurrenceDate,
    TimeRange Range);
