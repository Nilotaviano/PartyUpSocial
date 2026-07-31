using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Notifications;

/// <summary>
/// One computed recipient of one event — what <see cref="RecipientComputer"/> returns and
/// <see cref="Notification.From"/> materializes. It exists only inside the command that raised the
/// event: mute and the deletion-executor exclusion have already been applied, so every request in a
/// result set becomes a row (§9).
/// </summary>
public readonly record struct NotificationRequest(
    UserId RecipientUserId,
    NotificationType Type,
    NotificationSubject Subject);
