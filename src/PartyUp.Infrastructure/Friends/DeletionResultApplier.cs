using PartyUp.Application.Abstractions;
using PartyUp.Domain.Availability;
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
/// <see cref="ApplyAsync"/> also carries Phase 5's §11 step 5 tail (A-075): when
/// <see cref="DeletionExecutionResult.PurgedData"/> names <see cref="UserDataScope.Availability"/>, it
/// locks and deletes the subject's whole availability surface through
/// <see cref="IDeletionScopeProvider.LoadAvailabilityForUpdateAsync"/> and
/// <see cref="IAvailabilityRepository"/> — inside this same already-open transaction, so accounting for
/// that scope stays honest rather than a claimed-but-silent purge. <paramref name="scopeProvider"/> and
/// <paramref name="availability"/> default to <see langword="null"/> for callers constructed directly
/// without the widened availability protocol (the same optional-trailing-parameter shape
/// <c>ExecuteDeletionForUserUseCase</c> already uses for its own Phase 4 widening); production
/// resolution always supplies both through DI.
/// </summary>
public sealed class DeletionResultApplier(
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    INotificationRepository notifications,
    IReminderScheduleWriter reminderSchedule,
    IDeletionScopeProvider? scopeProvider = null,
    IAvailabilityRepository? availability = null) : IDeletionResultApplier
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

        if (result.PurgedData.Contains(UserDataScope.Availability) && scopeProvider is not null && availability is not null)
        {
            await ApplyAvailabilityDeletionAsync(subject, cancellationToken);
        }
    }

    private async Task ApplyAvailabilityDeletionAsync(UserId subject, CancellationToken cancellationToken)
    {
        AvailabilityDeletionScope scope =
            await scopeProvider!.LoadAvailabilityForUpdateAsync(subject, cancellationToken);

        foreach (AvailabilitySeries series in scope.Series)
        {
            availability!.RemoveSkipsFrom(series.Id, series.EffectiveFrom);
            availability.RemoveMaterializedBlocksFrom(series.Id, series.EffectiveFrom);
            availability.RemoveSeries(series);
        }

        foreach (AvailabilityBlock block in scope.Blocks)
        {
            // Materialized blocks were already removed above by RemoveMaterializedBlocksFrom; only
            // standalone one-time blocks are left to remove here.
            if (!block.IsMaterializedFromSeries)
            {
                availability!.RemoveBlock(block);
            }
        }
    }
}
