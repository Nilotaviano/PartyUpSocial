using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Friends;

public sealed class BlockEffectsExecutor(
    IBlockEffectScopeProvider scopeProvider,
    IFriendshipRepository friendships,
    IReminderScheduleWriter reminderSchedule,
    IClock clock) : IBlockEffectsExecutor
{
    public async Task ExecuteAsync(
        Block block,
        IReadOnlyList<Friendship> pairFriendships,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(pairFriendships);

        BlockEffectScope scope = await scopeProvider.LoadForUpdateAsync(
            block.BlockerId,
            block.BlockedId,
            cancellationToken);
        BlockEffectResult result = BlockEffects.Apply(
            block,
            pairFriendships,
            scope.Sessions,
            scope.Polls,
            clock);

        foreach (Friendship friendship in result.DeletedFriendships)
        {
            friendships.Remove(friendship);
        }

        foreach (SessionReminderCanceled cancellation in result.CanceledReminders)
        {
            await reminderSchedule.ApplyAsync(cancellation, cancellationToken);
        }

        foreach (Session session in scope.Sessions)
        {
            _ = session.DequeueEvents();
        }

        foreach (Poll poll in scope.Polls)
        {
            _ = poll.DequeueEvents();
        }
    }
}
