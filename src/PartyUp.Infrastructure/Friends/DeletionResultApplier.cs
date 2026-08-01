using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Infrastructure.Friends;

public sealed class DeletionResultApplier(
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    INotificationRepository notifications) : IDeletionResultApplier
{
    public Task ApplyAsync(
        DeletionExecutionResult result,
        UserId subject,
        CancellationToken cancellationToken) =>
        DeletionResultApplication.ApplyAsync(
            result,
            subject,
            friendships,
            blocks,
            notifications,
            cancellationToken);
}
