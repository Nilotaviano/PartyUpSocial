using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Abstractions;

public interface IDeletionResultApplier
{
    Task ApplyAsync(
        DeletionExecutionResult result,
        UserId subject,
        CancellationToken cancellationToken);
}

public static class DeletionResultApplication
{
    public static async Task ApplyAsync(
        DeletionExecutionResult result,
        UserId subject,
        IFriendshipRepository friendships,
        IBlockRepository blocks,
        INotificationRepository notifications,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        foreach (Friendship friendship in result.DeletedFriendships)
        {
            friendships.Remove(friendship);
        }

        foreach (Block block in result.DeletedBlocks)
        {
            blocks.Remove(block);
        }

        if (result.PurgedData.Contains(UserDataScope.Notifications))
        {
            await notifications.DeleteForRecipientAsync(subject, cancellationToken);
        }
    }
}
