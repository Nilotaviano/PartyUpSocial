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
            model["SubjectTitle"] = subjectTitle;
        }

        if (row.OptionStartTime is { } optionStart)
        {
            model["OptionStart"] = LocalizedInstantFormatter.FormatInstant(
                optionStart, row.RecipientZone, row.RecipientLocale);
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
    /// Ruling 2's <c>"{session|poll|group|friend}:{subjectId}"</c>. A friend-graph row (types 23/24)
    /// has no subject ref at all, so it keys on the actor instead; when even the actor id is absent (a
    /// blocked-pair-masked actor, §4's wire rule) the row's own notification id stands in — the tag
    /// still needs to exist and be stable, even though there is no "subject" left to name. The three
    /// D-207 group rows deep-link to <c>/groups/{id}</c> once the ref resolves and fall back to the
    /// plain <c>/groups</c> list otherwise — keyed on <see cref="NotificationReadModel.Type"/> rather
    /// than the generic friend fallback, since none of the three carry an actor-based destination.
    /// </summary>
    private static (string Tag, string Url) BuildTagAndUrl(NotificationReadModel row)
    {
        if (row.SubjectKind == NotificationSubjectKind.Session)
        {
            return ($"session:{row.SubjectId}", $"/sessions/{row.SubjectId}");
        }

        if (row.SubjectKind == NotificationSubjectKind.Poll)
        {
            return ($"poll:{row.SubjectId}", $"/polls/{row.SubjectId}");
        }

        if (row.SubjectKind == NotificationSubjectKind.Group)
        {
            return ($"group:{row.SubjectId}", $"/groups/{row.SubjectId}");
        }

        if (row.Type is NotificationType.FriendGroupInviteReceived
            or NotificationType.FriendGroupInviteAccepted
            or NotificationType.AudienceGroupDeleted)
        {
            return ($"group:{row.Id.Value}", "/groups");
        }

        return row.ActorUserId is { } actorId
            ? ($"friend:{actorId}", "/friends")
            : ($"friend:{row.Id.Value}", "/friends");
    }

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
