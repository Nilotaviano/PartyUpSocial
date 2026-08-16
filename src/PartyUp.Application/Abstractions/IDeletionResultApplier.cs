using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Abstractions;

public interface IDeletionResultApplier
{
    /// <summary>
    /// D-209: the owned groups this result names are hard-deleted through
    /// <see cref="ApplyContentRemovalAsync"/>, which this overload delegates its removal tail to —
    /// <see cref="DeletionExecutionResult.ContentRemoval"/> carries the very same
    /// <see cref="ContentRemovalResult.DeletedGroups"/> list, so this overload must not delete them a
    /// second time. Implemented in Infrastructure.
    /// </summary>
    Task ApplyAsync(
        DeletionExecutionResult result,
        UserId subject,
        CancellationToken cancellationToken);

    /// <summary>
    /// PHASE8_DESIGN.md §3 S2: the persistence tail of §11 steps 1–3 plus step 4's group severing on
    /// their own — canceling the reminder handoff each participation removal orphaned, and nothing
    /// else, plus D-209/D-180's group effect. This is the <em>only</em> place
    /// <see cref="ContentRemovalResult.DeletedGroups"/> is hard-deleted: the subject's own membership
    /// rows in others' groups are already removed on the tracked <c>FriendGroup</c> aggregates the
    /// executor mutated (the same pattern sessions/polls use), so only the owned groups named here
    /// need an explicit delete. The D-180 content removal (admin ban path) calls this directly — it
    /// produces no tombstone, no purge scopes, no friendship or block instructions and no journal
    /// entry, so it can never construct a <see cref="DeletionExecutionResult"/> — and
    /// <see cref="ApplyAsync"/> delegates its own removal tail here rather than keeping a second copy
    /// of the loop. Implemented in Infrastructure.
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
