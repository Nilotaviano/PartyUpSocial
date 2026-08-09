using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Abstractions;

public interface INotificationRepository
{
    void Add(Notification notification);

    /// <summary>S3: the dispatch job's per-row read, untracked — the job never writes through the aggregate itself.</summary>
    Task<Notification?> FindAsync(NotificationId id, CancellationToken cancellationToken);

    Task AddSteamMatchIfAbsentAsync(
        Notification notification,
        CancellationToken cancellationToken);

    Task DeleteForRecipientAsync(
        UserId recipientUserId,
        CancellationToken cancellationToken);
}
