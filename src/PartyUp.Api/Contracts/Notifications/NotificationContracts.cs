using System.Text.Json.Serialization;
using PartyUp.Api.Contracts.Sessions;
using PartyUp.Application.Notifications;
using PartyUp.Domain.Notifications;

namespace PartyUp.Api.Contracts.Notifications;

// ---- PHASE7_DESIGN.md §4: push subscriptions ------------------------------------------------------

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PushConfigResponse(string? VapidPublicKey, bool PushEnabled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreatePushSubscriptionRequest(string Channel, string Endpoint, string PublicKey, string AuthSecret);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreatePushSubscriptionResponse(Guid Id);

// ---- PHASE7_DESIGN.md §4: notification center -----------------------------------------------------

/// <summary>
/// The §4 masked-actor wire rule, exact: a blocked-pair-masked actor omits BOTH
/// <see cref="ActorUserId"/> and <see cref="ActorDisplayName"/>; a tombstoned or profile-incomplete
/// actor keeps <see cref="ActorUserId"/> and omits <see cref="ActorDisplayName"/> (client renders
/// Anonymous User). The §2 minimal form likewise omits <see cref="SubjectId"/>/<see cref="SubjectTitle"/>
/// rather than nulling them — every omitted field here uses <c>WhenWritingNull</c>, the
/// <c>SessionContracts.cs:251-253</c> precedent, so JSON absence (not <c>null</c>) is what the client
/// actually receives.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NotificationResponse(
    Guid Id,
    string Type,
    string CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReadAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ActorUserId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActorDisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubjectKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SubjectId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubjectTitle,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OptionStartTime,
    bool Unread)
{
    public static NotificationResponse From(NotificationReadModel row) =>
        new(
            row.Id.Value,
            NotificationWireFormat.Type(row.Type),
            SessionWireFormat.Format(row.CreatedAt),
            row.ReadAt is { } readAt ? SessionWireFormat.Format(readAt) : null,
            // §4 wire rule: the actor's masking Reason -- not merely whether an actor object was
            // built -- decides whether the id is present. Blocked drops it; named/tombstoned/
            // profile-incomplete keep it (NotificationReadUseCases.ProjectAsync already encodes this
            // exactly in ActorUserId).
            row.ActorUserId,
            row.Actor is { DisplayName: { } displayName } ? displayName.Value : null,
            row.HasSubject ? NotificationWireFormat.SubjectKind(row.SubjectKind) : null,
            row.SubjectId,
            row.SubjectTitle?.Value,
            row.OptionStartTime is { } optionStartTime ? SessionWireFormat.Format(optionStartTime) : null,
            row.IsUnread);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UnreadNotificationCountResponse(int Count);

public static class NotificationWireFormat
{
    public static string Type(NotificationType type) => type switch
    {
        NotificationType.SessionInvitation => "sessionInvitation",
        NotificationType.SessionJoinRequestReceived => "sessionJoinRequestReceived",
        NotificationType.SessionJoinRequestAccepted => "sessionJoinRequestAccepted",
        NotificationType.SessionJoinRequestRejected => "sessionJoinRequestRejected",
        NotificationType.WaitlistAdded => "waitlistAdded",
        NotificationType.WaitlistPromoted => "waitlistPromoted",
        NotificationType.WaitlistRejected => "waitlistRejected",
        NotificationType.WaitlistVacancy => "waitlistVacancy",
        NotificationType.SessionConfirmed => "sessionConfirmed",
        NotificationType.SessionUnconfirmed => "sessionUnconfirmed",
        NotificationType.SessionRescheduled => "sessionRescheduled",
        NotificationType.SessionCanceled => "sessionCanceled",
        NotificationType.SessionReminder => "sessionReminder",
        NotificationType.PollInvitation => "pollInvitation",
        NotificationType.PollOptionSuggested => "pollOptionSuggested",
        NotificationType.PollJoinRequestReceived => "pollJoinRequestReceived",
        NotificationType.PollJoinRequestAccepted => "pollJoinRequestAccepted",
        NotificationType.PollJoinRequestRejected => "pollJoinRequestRejected",
        NotificationType.PollDeadlineApproaching => "pollDeadlineApproaching",
        NotificationType.PollClosed => "pollClosed",
        NotificationType.PollOptionSelected => "pollOptionSelected",
        NotificationType.PollExpired => "pollExpired",
        NotificationType.PollCanceled => "pollCanceled",
        NotificationType.FriendRequestReceived => "friendRequestReceived",
        NotificationType.SteamImportMatch => "steamImportMatch",
        _ => type.ToString(),
    };

    public static string SubjectKind(Application.Notifications.NotificationSubjectKind kind) => kind switch
    {
        Application.Notifications.NotificationSubjectKind.Session => "session",
        Application.Notifications.NotificationSubjectKind.Poll => "poll",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
