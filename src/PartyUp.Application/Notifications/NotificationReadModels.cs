using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Notifications;

/// <summary>Which aggregate a row's subject fields describe, when it has one at all.</summary>
public enum NotificationSubjectKind
{
    None = 0,
    Session = 1,
    Poll = 2,
}

/// <summary>
/// One row of the notification center, resolved for a specific recipient (PHASE7_DESIGN.md §2).
/// <see cref="Actor"/> and the subject fields are absent — not null-but-present — exactly when the
/// wire shape must omit them (S4's mapping): a friend-graph row with no actor ref carries no
/// <see cref="Actor"/>, and a row whose subject the recipient cannot reach carries
/// <see cref="SubjectKind"/> = <see cref="NotificationSubjectKind.None"/> with every other subject
/// field null. The row itself — id, type, timestamps — is always present and always
/// unread-countable; masking is render-time presentation on top of a real record (D-086). The
/// minimal form's absent actor is the §4 wire rule's "Anonymous actor" shape — both
/// <see cref="Actor"/> and <see cref="ActorUserId"/> absent, the client renders Anonymous — not merely
/// the no-actor-ref case.
/// </summary>
public sealed record NotificationReadModel(
    NotificationId Id,
    NotificationType Type,
    Instant CreatedAt,
    Instant? ReadAt,
    MaskedIdentity? Actor,
    Guid? ActorUserId,
    NotificationSubjectKind SubjectKind,
    Guid? SubjectId,
    SessionTitle? SubjectTitle,
    Instant? OptionStartTime,
    string RecipientLocale,
    DateTimeZone RecipientZone)
{
    public bool IsUnread => ReadAt is null;

    public bool HasSubject => SubjectKind != NotificationSubjectKind.None;
}
