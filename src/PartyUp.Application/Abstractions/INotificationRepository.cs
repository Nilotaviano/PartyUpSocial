using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Abstractions;

public interface INotificationRepository
{
    void Add(Notification notification);

    Task AddSteamMatchIfAbsentAsync(
        Notification notification,
        CancellationToken cancellationToken);

    Task DeleteForRecipientAsync(
        UserId recipientUserId,
        CancellationToken cancellationToken);
}
