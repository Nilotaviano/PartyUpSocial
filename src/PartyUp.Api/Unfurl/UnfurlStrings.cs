using System.Globalization;
using NodaTime;
using NodaTime.Text;

namespace PartyUp.Api.Unfurl;

/// <summary>The three locales D-224's card renders in; anything else falls back to English.</summary>
public enum UnfurlLocale
{
    /// <summary>en.</summary>
    English = 0,

    /// <summary>pt-BR.</summary>
    BrazilianPortuguese = 1,

    /// <summary>es.</summary>
    Spanish = 2,
}

/// <summary>
/// The server-side string set for the unfurl metadata and card. The SPA's i18next catalogs are not
/// reachable from the API, so this small static dictionary is the API's own copy — five strings and
/// one date format per locale, rendered in the ORGANIZER's locale because the viewer is unknown.
/// </summary>
public static class UnfurlStrings
{
    private static readonly CultureInfo[] Cultures =
    [
        CultureInfo.GetCultureInfo("en"),
        CultureInfo.GetCultureInfo("pt-BR"),
        CultureInfo.GetCultureInfo("es"),
    ];

    private static readonly string[] MomentPatterns =
    [
        "ddd d MMM '·' HH:mm",
        "ddd, d 'de' MMM '·' HH:mm",
        "ddd d 'de' MMM '·' HH:mm",
    ];

    private static readonly string[] GoingFormats = ["{0} going", "{0} vão", "{0} van"];

    private static readonly string[] VoteInvitations =
    [
        "Vote on when to play",
        "Vote em quando jogar",
        "Vota cuándo jugar",
    ];

    private static readonly string[] ClosesFormats = ["closes {0}", "encerra {0}", "cierra {0}"];

    private static readonly string[] UnavailableNotices =
    [
        "This invite is no longer available",
        "Este convite não está mais disponível",
        "Esta invitación ya no está disponible",
    ];

    private static readonly string[] InviteLinkTitles = ["Invite link", "Link de convite", "Enlace de invitación"];

    /// <summary>Maps a stored BCP-47 tag onto the three supported locales; unknown tags become English.</summary>
    public static UnfurlLocale Resolve(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return UnfurlLocale.English;
        }

        if (locale.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            return UnfurlLocale.BrazilianPortuguese;
        }

        return locale.StartsWith("es", StringComparison.OrdinalIgnoreCase)
            ? UnfurlLocale.Spanish
            : UnfurlLocale.English;
    }

    /// <summary>"N going" — the active Going count on a live session card.</summary>
    public static string Going(UnfurlLocale locale, int count) =>
        string.Format(Culture(locale), GoingFormats[(int)locale], count);

    /// <summary>The live poll card's call to action.</summary>
    public static string VoteOnWhenToPlay(UnfurlLocale locale) => VoteInvitations[(int)locale];

    /// <summary>"closes {when}" — the poll's voting deadline, already formatted.</summary>
    public static string Closes(UnfurlLocale locale, string when) =>
        string.Format(Culture(locale), ClosesFormats[(int)locale], when);

    /// <summary>The single generic notice every non-live link unfurls as.</summary>
    public static string Unavailable(UnfurlLocale locale) => UnavailableNotices[(int)locale];

    /// <summary>The title a generic card carries — it names no session and no poll.</summary>
    public static string InviteLink(UnfurlLocale locale) => InviteLinkTitles[(int)locale];

    /// <summary>
    /// A date and time in the organizer's zone and locale, with the zone's UTC offset spelled out —
    /// the reader of an unfurled preview has no other way to know which clock it is in.
    /// </summary>
    public static string Moment(UnfurlLocale locale, Instant instant, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        ZonedDateTime zoned = instant.InZone(zone);
        CultureInfo culture = Culture(locale);
        string moment = LocalDateTimePattern
            .Create(MomentPatterns[(int)locale], culture)
            .Format(zoned.LocalDateTime);
        string offset = OffsetPattern.Create("m", culture).Format(zoned.Offset);
        return $"{moment} UTC{offset}";
    }

    private static CultureInfo Culture(UnfurlLocale locale) => Cultures[(int)locale];
}
