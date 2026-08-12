using System.Globalization;
using NodaTime;

namespace PartyUp.Application.Notifications;

/// <summary>
/// D-060/spec §8.5: an instant renders in the recipient's own zone and locale. Shared by
/// <see cref="Push.PushPayloadFactory"/> and the D-189 invite email dispatch path so the
/// locale-to-culture mapping lives in exactly one place.
/// </summary>
internal static class LocalizedInstantFormatter
{
    internal static string FormatInstant(Instant instant, DateTimeZone zone, string locale)
    {
        ZonedDateTime zoned = instant.InZone(zone);
        DateTime local = zoned.ToDateTimeUnspecified();
        return local.ToString("f", ResolveCulture(locale));
    }

    internal static CultureInfo ResolveCulture(string locale) => locale switch
    {
        "pt-BR" => CultureInfo.GetCultureInfo("pt-BR"),
        "es" => CultureInfo.GetCultureInfo("es-ES"),
        _ => CultureInfo.GetCultureInfo("en-US"),
    };
}
