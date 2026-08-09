using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Notifications;
using PartyUp.Application.Push;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Infrastructure.Jobs;

/// <summary>
/// The Hangfire dispatch job (PHASE7_DESIGN.md §3): pages every <c>dispatch_state = 0</c> row and
/// resolves each to exactly one terminal transition before the next tick — Skipped (past the 24 h
/// lateness ceiling, muted, or subject-unreachable) or Dispatched (fanned out, or trivially so with
/// zero live subscriptions). A claim loser and a crash-recovered row both send nothing; the coverage
/// UPDATE is what completes them either way (see <see cref="INotificationDispatchRepository.TryCompleteDispatchIfCoveredAsync"/>).
/// </summary>
public sealed partial class PushDispatchJob(
    INotificationDispatchRepository dispatch,
    INotificationRepository notifications,
    INotificationPushSendRepository pushSends,
    IPushSubscriptionRepository subscriptions,
    NotificationReadUseCases notificationReads,
    PushPayloadFactory payloadFactory,
    IPushSender sender,
    IClock clock,
    ILogger<PushDispatchJob> logger)
{
    /// <summary>Ruling 6, owner-ratified 2026-08-09: a Pending row older than this is noise, unsent.</summary>
    private static readonly Duration LatenessCeiling = Duration.FromHours(24);

    /// <summary>Bounded per tick; a backlog beyond this is picked up on the next minutely cadence.</summary>
    private const int BatchSize = 200;

    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // §8: absent/partial VAPID configuration must leave every Pending row untouched, bounded only
        // by the lateness ceiling -- not skipped, not claimed, not touched at all.
        if (!sender.IsConfigured)
        {
            return;
        }

        Instant now = clock.GetCurrentInstant();
        IReadOnlyList<NotificationId> page = await dispatch.PagePendingIdsAsync(BatchSize, cancellationToken);

        foreach (NotificationId id in page)
        {
            await ProcessAsync(id, now, cancellationToken);
        }
    }

    private async Task ProcessAsync(NotificationId id, Instant now, CancellationToken cancellationToken)
    {
        Notification? notification = await notifications.FindAsync(id, cancellationToken);
        if (notification is null)
        {
            // Nothing left to transition; the row itself is gone (deletion purge raced this tick).
            return;
        }

        if (now - notification.CreatedAt > LatenessCeiling)
        {
            await dispatch.TryMarkSkippedAsync(id, cancellationToken);
            return;
        }

        // SessionReminder (type 12) is the D-090/ReminderRules exemption the write path already
        // carries -- the sole type this dispatch-time re-check never applies to.
        if (notification.Type != NotificationType.SessionReminder)
        {
            bool muted = await dispatch.IsMutedAsync(
                notification.RecipientUserId, notification.SessionId, notification.PollId, cancellationToken);
            if (muted)
            {
                await dispatch.TryMarkSkippedAsync(id, cancellationToken);
                return;
            }
        }

        bool reachable = await notificationReads.IsSubjectReachableAsync(
            notification, notification.RecipientUserId, cancellationToken);
        if (!reachable)
        {
            await dispatch.TryMarkSkippedAsync(id, cancellationToken);
            return;
        }

        IReadOnlyList<PushSubscriptionTarget> targets =
            await subscriptions.ListActiveForUserAsync(notification.RecipientUserId, cancellationToken);

        if (targets.Count > 0)
        {
            NotificationReadModel row = await notificationReads.ProjectAsync(
                notification, notification.RecipientUserId, cancellationToken);
            PushPayload payload = payloadFactory.Build(row);

            foreach (PushSubscriptionTarget target in targets)
            {
                await SendToSubscriptionAsync(id, target, payload, cancellationToken);
            }
        }

        // The whole coverage rule, in one atomic UPDATE: zero-subscription rows satisfy it trivially,
        // crash-recovered rows satisfy it because their prior send-log rows already exist, and a claim
        // loser needs nothing further -- this call is what completes it in every case.
        await dispatch.TryCompleteDispatchIfCoveredAsync(id, notification.RecipientUserId, cancellationToken);
    }

    private async Task SendToSubscriptionAsync(
        NotificationId id,
        PushSubscriptionTarget target,
        PushPayload payload,
        CancellationToken cancellationToken)
    {
        // The claim insert commits before the send (its own statement, no ambient transaction) -- a
        // claim loser sends nothing and writes no outcome; the existing row is what completes coverage.
        Guid? claimed = await pushSends.ClaimAsync(id, target.Id, clock, cancellationToken);
        if (claimed is not { } sendLogId)
        {
            return;
        }

        PushSendResult result;
        try
        {
            result = await sender.SendAsync(target, payload, cancellationToken);
        }
        catch (Exception exception)
        {
            // A truly unexpected sender failure (a bug, not an ordinary push-service rejection --
            // IPushSender's contract already turns those into a PushSendResult): never log the
            // exception's message verbatim, only the subscription id, since some transports fold
            // request details into their exception text.
            LogSendFailedUnexpectedly(logger, target.Id.Value, exception.GetType().Name);
            result = new PushSendResult(PushSendOutcome.Failed, null);
        }

        // The outcome write is a second transaction keyed on the send-log row id -- idempotent to
        // re-write, and never re-derives which row it belongs to.
        await pushSends.UpdateOutcomeAsync(sendLogId, result.Outcome, cancellationToken);

        if (result.Outcome == PushSendOutcome.Gone)
        {
            await subscriptions.RevokeAsync(target.Id, clock, cancellationToken);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Push send to subscription {SubscriptionId} failed unexpectedly ({ExceptionType})")]
    private static partial void LogSendFailedUnexpectedly(
        ILogger logger,
        Guid subscriptionId,
        string exceptionType);
}
