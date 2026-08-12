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

    /// <summary>
    /// PHASE8_DESIGN.md §3 S2: the persistence tail of §11 steps 1–3 on their own — canceling the
    /// reminder handoff each participation removal orphaned, and nothing else. The D-180 content
    /// removal calls it directly (it produces no tombstone, no purge scopes, no friendship or block
    /// instructions and no journal entry, so it can never construct a
    /// <see cref="DeletionExecutionResult"/>), and <see cref="ApplyAsync"/> delegates its own steps
    /// 1–3 tail here rather than keeping a second copy of the loop.
    /// </summary>
    Task ApplyContentRemovalAsync(
        ContentRemovalResult result,
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
