using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;

namespace PartyUp.Infrastructure.Persistence.Converters;

public sealed class DateTimeZoneConverter() : ValueConverter<DateTimeZone, string>(
    zone => zone.Id,
    id => DateTimeZoneProviders.Tzdb[id]);
