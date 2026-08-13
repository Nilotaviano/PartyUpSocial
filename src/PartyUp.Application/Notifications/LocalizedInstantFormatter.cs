using System.Globalization;
using NodaTime;

namespace PartyUp.Application.Notifications;

/// <summary>
/// D-060/spec §8.5: an instant renders in the recipient's own zone and locale. Shared by
/// <see cref="Push.PushPayloadFactory"/> and the D-189 invite email dispatch path. The account
/// deletion grace-deadline email (<see cref="Deletion.RequestAccountDeletionUseCase"/>) keeps a
/// second, deliberate locale-to-culture-and-pattern map because it needs a trailing zone
/// abbreviation this formatter's "f" shape does not carry.
/// </summary>
internal static class LocalizedInstantFormatter
{
    internal static string FormatInstant(Instant instant, DateTimeZone zone, string locale)
    {
        ZonedDateTime zoned = instant.InZone(zone);
        DateTime local = zoned.ToDateTimeUnspecified();
        // "g" (short date + short time) is the S6-chosen shape, kept close to the web UI's
        // `dateStyle:'medium', timeStyle:'short'` render of the same field.
        return local.ToString("g", ResolveCulture(locale));
    }

    internal static CultureInfo ResolveCulture(string locale) => locale switch
    {
        "pt-BR" => CultureInfo.GetCultureInfo("pt-BR"),
        "es" => CultureInfo.GetCultureInfo("es-ES"),
        _ => CultureInfo.GetCultureInfo("en-US"),
    };
}
