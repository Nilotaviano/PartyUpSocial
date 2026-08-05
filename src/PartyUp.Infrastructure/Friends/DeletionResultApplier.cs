using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Infrastructure.Friends;

/// <summary>
/// Extends the Phase 3 friendship/block/notification-purge apply step with the Phase 4 session effects
/// (DOMAIN_MODEL.md:239-249): the aggregate mutations themselves (cancel future owned, remove from
/// other not-started, lapse the subject's own open join requests, retain in-progress/Completed) already
/// happened on the tracked <c>Session</c> instances inside <c>DeletionExecutor.Execute</c>, so EF picks
/// them up at the transaction's single <c>SaveChanges</c>. What is left for this applier is canceling
/// the reminder handoff each removal produced, through the same shipped <see cref="IReminderScheduleWriter"/>
/// port <c>BlockEffectsExecutor</c> uses — then tombstoning proceeds in the established executor.
/// </summary>
public sealed class DeletionResultApplier(
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    INotificationRepository notifications,
    IReminderScheduleWriter reminderSchedule) : IDeletionResultApplier
{
    public async Task ApplyAsync(
        DeletionExecutionResult result,
        UserId subject,
        CancellationToken cancellationToken)
    {
        await DeletionResultApplication.ApplyAsync(
            result,
            subject,
            friendships,
            blocks,
            notifications,
            cancellationToken);

        foreach (SessionReminderCanceled cancellation in result.CanceledReminders)
        {
            await reminderSchedule.ApplyAsync(cancellation, cancellationToken);
        }
    }
}
