using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;
using PartyUp.Infrastructure.Persistence;

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
/// that scope stays honest rather than a claimed-but-silent purge. Both are <strong>required</strong>
/// (round-1 finding F7): an optional default made a silent purge-skip reachable from any direct
/// construction site that forgot to supply them, which is exactly the "accounting stays honest" failure
/// mode this class exists to prevent. Production resolution supplies both through DI.
///
/// <para>
/// PHASE7_DESIGN.md §1 extends the same tail: <see cref="UserDataScope.PushSubscriptions"/> and
/// <see cref="UserDataScope.NotificationMutes"/> are purged here too, ahead of the shared helper's
/// <see cref="UserDataScope.Notifications"/> purge -- send-log rows follow their subscription's
/// cascade FK, so no separate send-log delete is needed.
/// </para>
///
/// <para>
/// AVATARS_DESIGN.md §3.4 extends the tail once more: <see cref="UserDataScope.AvatarSources"/> is
/// purged through <see cref="IAvatarSourceRepository"/>, deleting every captured picture the user's
/// linked accounts ever supplied. <c>User.MarkDeleted</c> already nulled the tracked user's
/// <c>avatar_source_id</c> reference in memory, but that change is only flushed to the database by
/// the SaveChanges the caller's <c>IUnitOfWork.ExecuteAsync</c> issues at the very end of the
/// transaction. <c>avatar_sources.ExecuteDeleteAsync</c> below is a raw SQL DELETE that runs
/// immediately, so without an explicit flush first, the DB-only <c>ON DELETE SET NULL</c> foreign key
/// on <c>users.avatar_source_id</c> would still see the old (non-null) value, fire, and bump the
/// user row's <c>xmin</c> behind the tracked entity's back -- turning the deferred SaveChanges into a
/// spurious concurrency failure.
/// </para>
/// </summary>
public sealed class DeletionResultApplier(
    AppDbContext context,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    INotificationRepository notifications,
    IReminderScheduleWriter reminderSchedule,
    IDeletionScopeProvider scopeProvider,
    IAvailabilityRepository availability,
    IPushSubscriptionRepository pushSubscriptions,
    INotificationMuteRepository notificationMutes,
    IAvatarSourceRepository avatarSources,
    IFeedbackRepository feedback) : IDeletionResultApplier
{
    public async Task ApplyAsync(
        DeletionExecutionResult result,
        UserId subject,
        CancellationToken cancellationToken)
    {
        if (result.PurgedData.Contains(UserDataScope.PushSubscriptions))
        {
            await pushSubscriptions.DeleteForUserAsync(subject, cancellationToken);
        }

        if (result.PurgedData.Contains(UserDataScope.NotificationMutes))
        {
            await notificationMutes.DeleteForUserAsync(subject, cancellationToken);
        }

        if (result.PurgedData.Contains(UserDataScope.AvatarSources))
        {
            // Flush the tracked user's (now-nulled) avatar_source_id to the database before the raw
            // delete below, so the FK's ON DELETE SET NULL trigger has nothing left to mutate.
            await context.SaveChangesAsync(cancellationToken);
            await avatarSources.DeleteForUserAsync(subject, cancellationToken);
        }

        await DeletionResultApplication.ApplyAsync(
            result,
            subject,
            friendships,
            blocks,
            notifications,
            cancellationToken);

        await ApplyContentRemovalAsync(result.ContentRemoval, subject, cancellationToken);

        if (result.PurgedData.Contains(UserDataScope.Availability))
        {
            await ApplyAvailabilityDeletionAsync(subject, cancellationToken);
        }

        // D-216: anonymize, do not erase — feedback rows keep their message and lose only their
        // submitter reference. Deliberately not a UserDataScope: those name purges, and nothing here
        // is purged. Deletion-only: D-180's content removal (ApplyContentRemovalAsync) leaves a
        // banned account's feedback attributed, exactly like the rest of its retained history.
        await feedback.AnonymizeForUserAsync(subject, cancellationToken);

        ClearLastSeen(subject);
    }

    /// <summary>
    /// The §11 steps 1–3 plus step 4's group severing on its own, which is the whole of what D-180's
    /// content removal has to persist beyond the tracked aggregate mutations: the reminder handoff each
    /// participation removal orphaned, and the hard delete of every group the subject owned (D-209).
    /// The subject's own membership rows in other people's groups are already gone from the tracked
    /// <c>FriendGroup</c> aggregates, exactly like session/poll participation removals. No purge
    /// scopes, no friendship or block removal, no availability cascade and no last-seen clearing — a
    /// banned account stays live and countable as active. <see cref="ApplyAsync"/> delegates here, so
    /// this is the only place the group delete happens on either path.
    /// </summary>
    public async Task ApplyContentRemovalAsync(
        ContentRemovalResult result,
        UserId subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        foreach (SessionReminderCanceled cancellation in result.CanceledReminders)
        {
            await reminderSchedule.ApplyAsync(cancellation, cancellationToken);
        }

        if (result.DeletedGroups.Count > 0)
        {
            // Membership rows and every session/poll audience junction row targeting the group follow
            // the cascade FKs (D-199/D-205).
            FriendGroupId[] deletedIds = [.. result.DeletedGroups];
            List<FriendGroup> deleted = await context.FriendGroups
                .Where(group => deletedIds.Contains(group.Id))
                .ToListAsync(cancellationToken);
            context.FriendGroups.RemoveRange(deleted);
        }
    }

    /// <summary>
    /// PHASE8_DESIGN.md §2 Q1: a tombstone must not keep counting as an active user, so the deletion
    /// path nulls <c>users.last_seen_at</c> with the rest of the PII. It is a shadow property, so it
    /// is cleared on the tracked entity the executor just tombstoned — a raw UPDATE here would bump
    /// the row's <c>xmin</c> behind that entity's back and turn the caller's deferred SaveChanges into
    /// a spurious concurrency failure.
    /// </summary>
    private void ClearLastSeen(UserId subject)
    {
        foreach (EntityEntry<User> entry in context.ChangeTracker.Entries<User>())
        {
            if (entry.Entity.Id == subject)
            {
                entry.Property<Instant?>(UserActivityWriter.LastSeenProperty).CurrentValue = null;
            }
        }
    }

    private async Task ApplyAvailabilityDeletionAsync(UserId subject, CancellationToken cancellationToken)
    {
        AvailabilityDeletionScope scope =
            await scopeProvider.LoadAvailabilityForUpdateAsync(subject, cancellationToken);

        foreach (AvailabilitySeries series in scope.Series)
        {
            availability.RemoveSkipsFrom(series.Id, series.EffectiveFrom);
            availability.RemoveMaterializedBlocksFrom(series.Id, series.EffectiveFrom);
            availability.RemoveSeries(series);
        }

        foreach (AvailabilityBlock block in scope.Blocks)
        {
            // Materialized blocks were already removed above by RemoveMaterializedBlocksFrom; only
            // standalone one-time blocks are left to remove here.
            if (!block.IsMaterializedFromSeries)
            {
                availability.RemoveBlock(block);
            }
        }
    }
}
