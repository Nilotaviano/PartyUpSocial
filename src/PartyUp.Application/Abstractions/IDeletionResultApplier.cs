using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Abstractions;

public interface IDeletionResultApplier
{
    /// <summary>
    /// D-209: <see cref="DeletionExecutionResult.DeletedGroups"/> must be hard-deleted here too — the
    /// subject's own membership rows in others' groups are already removed on the tracked
    /// <c>FriendGroup</c> aggregates the executor mutated (the same pattern sessions/polls use), so
    /// only the owned groups named in <see cref="DeletionExecutionResult.DeletedGroups"/> need an
    /// explicit delete. Implemented in Infrastructure (Task 5).
    /// </summary>
    Task ApplyAsync(
        DeletionExecutionResult result,
        UserId subject,
        CancellationToken cancellationToken);

    /// <summary>
    /// PHASE8_DESIGN.md §3 S2: the persistence tail of §11 steps 1–4 on their own — canceling the
    /// reminder handoff each participation removal orphaned, and nothing else, plus D-209/D-180's
    /// group effect: <see cref="ContentRemovalResult.DeletedGroups"/> must be hard-deleted here too,
    /// the same way <see cref="ApplyAsync"/> handles <see cref="DeletionExecutionResult.DeletedGroups"/>
    /// (the subject's own membership rows in others' groups are already removed on the tracked
    /// <c>FriendGroup</c> aggregates the executor mutated, so only the owned groups named here need an
    /// explicit delete). The D-180 content removal (admin ban path) calls this directly — it produces
    /// no tombstone, no purge scopes, no friendship or block instructions and no journal entry, so it
    /// can never construct a <see cref="DeletionExecutionResult"/> — and <see cref="ApplyAsync"/>
    /// delegates its own steps 1–4 tail here rather than keeping a second copy of the loop.
    /// Implemented in Infrastructure (Task 5).
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
