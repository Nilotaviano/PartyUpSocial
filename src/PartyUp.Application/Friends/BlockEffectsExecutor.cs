using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Friends;

public sealed class BlockEffectsExecutor(
    IBlockEffectScopeProvider scopeProvider,
    IFriendshipRepository friendships,
    IReminderScheduleWriter reminderSchedule,
    IClock clock,
    ISessionNotificationMaterializer? materializer = null) : IBlockEffectsExecutor
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
            scope.Groups,
            clock);

        foreach (Friendship friendship in result.DeletedFriendships)
        {
            friendships.Remove(friendship);
        }

        foreach (SessionReminderCanceled cancellation in result.CanceledReminders)
        {
            await reminderSchedule.ApplyAsync(cancellation, cancellationToken);
        }

        // D-105: blocking itself stays silent (no cause, no actor), but the derived Session events a
        // removal/lapse produces (e.g. SessionUnconfirmed) are ordinary state-change notifications —
        // P4-NOTIFY-01 materializes them through the same masked recipient contract every other Session
        // command uses. The real materializer lands in a later slice; this drains through the port so
        // no event is ever silently discarded once one is registered.
        foreach (Session session in scope.Sessions)
        {
            IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
            if (materializer is not null && events.Count > 0)
            {
                await materializer.MaterializeAsync(session, events, cancellationToken);
            }
        }

        foreach (Poll poll in scope.Polls)
        {
            _ = poll.DequeueEvents();
        }
    }
}
