using System.Globalization;
using NodaTime;
using PartyUp.Application.Notifications;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Push;

/// <summary>
/// Builds §3.1's <c>{title, body, url, tag}</c> from an already-projected §2 row — masking and the
/// subject-access gate have both applied before this is ever called (ruling 5), so this class only
/// renders what the row already carries. Called only for a row the dispatch job has already proven
/// reachable and unmuted: <see cref="Build"/> is not a second access check.
/// </summary>
public sealed class PushPayloadFactory(IPushTemplateRenderer renderer)
{
    public PushPayload Build(NotificationReadModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        Dictionary<string, string> model = [];
        if (row.SubjectTitle is { } subjectTitle)
        {
            model["SubjectTitle"] = subjectTitle.Value;
        }

        if (row.OptionStartTime is { } optionStart)
        {
            model["OptionStart"] = FormatInstant(optionStart, row.RecipientZone, row.RecipientLocale);
        }

        if (row.Actor is { } actor)
        {
            model["ActorName"] = actor.IsAnonymous
                ? AnonymousName(row.RecipientLocale)
                : actor.DisplayName!.Value.Value;
        }

        (string renderedTitle, string renderedBody) = renderer.Render(row.Type, row.RecipientLocale, model);
        (string tag, string url) = BuildTagAndUrl(row);

        return new PushPayload(renderedTitle, renderedBody, url, tag);
    }

    /// <summary>
    /// Ruling 2's <c>"{session|poll|friend}:{subjectId}"</c>. A friend-graph row (types 23/24) has no
    /// subject ref at all, so it keys on the actor instead; when even the actor id is absent (a
    /// blocked-pair-masked actor, §4's wire rule) the row's own notification id stands in — the tag
    /// still needs to exist and be stable, even though there is no "subject" left to name.
    /// </summary>
    private static (string Tag, string Url) BuildTagAndUrl(NotificationReadModel row) => row.SubjectKind switch
    {
        NotificationSubjectKind.Session => ($"session:{row.SubjectId}", $"/sessions/{row.SubjectId}"),
        NotificationSubjectKind.Poll => ($"poll:{row.SubjectId}", $"/polls/{row.SubjectId}"),
        _ => row.ActorUserId is { } actorId
            ? ($"friend:{actorId}", "/friends")
            : ($"friend:{row.Id.Value}", "/friends"),
    };

    /// <summary>D-060/spec §8.5: the instant renders in the recipient's own zone and locale — the same inputs §2 already resolved.</summary>
    private static string FormatInstant(Instant instant, DateTimeZone zone, string locale)
    {
        ZonedDateTime zoned = instant.InZone(zone);
        DateTime local = zoned.ToDateTimeUnspecified();
        return local.ToString("f", ResolveCulture(locale));
    }

    private static CultureInfo ResolveCulture(string locale) => locale switch
    {
        "pt-BR" => CultureInfo.GetCultureInfo("pt-BR"),
        "es" => CultureInfo.GetCultureInfo("es-ES"),
        _ => CultureInfo.GetCultureInfo("en-US"),
    };

    /// <summary>
    /// A structural three-word fallback, not long-form content — kept in code rather than as a
    /// template resource, the same way <c>MaskedIdentity</c>'s own doc notes "Anonymous User" is
    /// rendered by each presentation layer in the viewer's locale rather than carried on the record.
    /// </summary>
    private static string AnonymousName(string locale) => locale switch
    {
        "pt-BR" => "Usuário Anônimo",
        "es" => "Usuario Anónimo",
        _ => "Anonymous User",
    };
}
