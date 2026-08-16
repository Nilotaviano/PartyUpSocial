using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Notifications;
using PartyUp.Application.Push;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Jobs;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Push;

namespace PartyUp.Api.IntegrationTests.Notifications;

/// <summary>
/// PHASE7_DESIGN.md §3/§7 S3's dispatch gate: the full per-row outcome machine, proven against a real
/// Postgres instance so the two atomic SQL transitions (<c>TryMarkSkippedAsync</c>,
/// <c>TryCompleteDispatchIfCoveredAsync</c>) and the unique-constraint-backed claim are the actual
/// guarantee, not a mock standing in for one. <see cref="FakePushSender"/> replaces the transport only —
/// every repository and the read projection are the real, DI-wired production adapters, resolved per
/// test from the factory's container and assembled into the job by hand (never through the container's
/// own <see cref="IPushSender"/> registration, which the "Testing" host leaves on the "log" default and
/// which this suite must not depend on).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PushDispatchJobTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ZeroSubscriptions_DispatchesWithNoSendLogRows()
    {
        UserId recipientId = await _factory.SeedUserAsync(email: "zero-sub@example.test");
        Session session = await SeedSessionWithOrganizerAsync(recipientId);
        Guid notificationId = await SeedNotificationAsync(recipientId, NotificationType.SessionConfirmed, session.Id);

        var sender = new FakePushSender();
        await RunJobAsync(sender);

        Assert.Equal(1, await ReadDispatchStateAsync(notificationId));
        Assert.Equal(0, await CountSendLogRowsAsync(notificationId));
        Assert.Equal(0, sender.SendCount);
    }

    /// <summary>
    /// F2/§8: absent VAPID configuration must leave every Pending row untouched -- not skipped, not
    /// claimed -- so a Production container the owner has not yet provisioned never burns rows.
    /// <see cref="WebPushSender"/> is the real Production default (root <c>appsettings.json</c>); an
    /// empty configuration leaves <c>IsConfigured</c> false, the same gate <c>ExecuteAsync</c> checks
    /// before paging at all.
    /// </summary>
    [Fact]
    public async Task UnconfiguredWebPushSender_LeavesThePendingRowUntouched_F2()
    {
        UserId recipientId = await _factory.SeedUserAsync(email: "unconfigured-vapid@example.test");
        Session session = await SeedSessionWithOrganizerAsync(recipientId);
        Guid notificationId = await SeedNotificationAsync(recipientId, NotificationType.SessionConfirmed, session.Id);
        await SeedSubscriptionAsync(recipientId, "unconfigured-vapid-sub");

        IConfiguration emptyConfiguration = new ConfigurationBuilder().Build();
        var sender = new WebPushSender(emptyConfiguration);
        Assert.False(sender.IsConfigured);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        PushDispatchJob job = BuildJobWithSender(scope.ServiceProvider, sender);
        await job.ExecuteAsync(default);

        Assert.Equal(0, await ReadDispatchStateAsync(notificationId));
        Assert.Equal(0, await CountSendLogRowsAsync(notificationId));
    }

    [Fact]
    public async Task StalePendingRow_PastTheLatenessCeiling_IsSkipped_Ruling6()
    {
        UserId recipientId = await _factory.SeedUserAsync(email: "stale@example.test");
        Session session = await SeedSessionWithOrganizerAsync(recipientId);
        Guid notificationId = await SeedNotificationAsync(recipientId, NotificationType.SessionConfirmed, session.Id);
        await SeedSubscriptionAsync(recipientId, "stale-sub");

        _factory.Clock.Advance(Duration.FromHours(25));

        var sender = new FakePushSender();
        await RunJobAsync(sender);

        Assert.Equal(2, await ReadDispatchStateAsync(notificationId));
        Assert.Equal(0, sender.SendCount);
    }

    [Fact]
    public async Task UnreachableSubject_IsSkipped_D076()
    {
        UserId strangerId = await _factory.SeedUserAsync(email: "stranger@example.test");
        UserId organizerId = await _factory.SeedUserAsync(email: "private-organizer@example.test");
        // The stranger holds no relationship to this private session at all -- the same "no access"
        // boundary §2's projection already gates reads through.
        Session session = await SeedSessionWithOrganizerAsync(organizerId, isPrivate: true);
        Guid notificationId = await SeedNotificationAsync(strangerId, NotificationType.SessionCanceled, session.Id);
        await SeedSubscriptionAsync(strangerId, "unreachable-sub");

        var sender = new FakePushSender();
        await RunJobAsync(sender);

        Assert.Equal(2, await ReadDispatchStateAsync(notificationId));
        Assert.Equal(0, sender.SendCount);
    }

    /// <summary>
    /// §3's re-check: write-time mute (§9) cannot cover a mute created after the record, so the row's
    /// own creation must precede the mute here for the test to prove dispatch-time re-evaluation rather
    /// than the write-time suppression the record path already covers.
    /// </summary>
    [Fact]
    public async Task PostRecordMuteSuppressesThePush_WhileTheReminderStillDispatches_D090_D113_D082()
    {
        UserId recipientId = await _factory.SeedUserAsync(email: "muted@example.test");
        Session session = await SeedSessionWithOrganizerAsync(recipientId);
        await SeedSubscriptionAsync(recipientId, "muted-sub");

        Guid mutedNotificationId =
            await SeedNotificationAsync(recipientId, NotificationType.SessionCanceled, session.Id);
        Guid reminderNotificationId =
            await SeedNotificationAsync(recipientId, NotificationType.SessionReminder, session.Id);

        await SeedSessionMuteAsync(recipientId, session.Id);

        var sender = new FakePushSender();
        await RunJobAsync(sender);

        Assert.Equal(2, await ReadDispatchStateAsync(mutedNotificationId));
        Assert.Equal(0, await CountSendLogRowsAsync(mutedNotificationId));

        Assert.Equal(1, await ReadDispatchStateAsync(reminderNotificationId));
        Assert.Equal(1, await CountSendLogRowsAsync(reminderNotificationId));
        Assert.Equal(1, sender.SendCount);
    }

    /// <summary>
    /// §3's third mute arm, post-finalization: the mute row was re-parented onto the session
    /// (<c>AttachToSession</c>) exactly as a real finalization would, and the notification row still
    /// names the original poll — the join must reach it through <c>sessions.origin_poll_id</c>.
    /// </summary>
    [Fact]
    public async Task PostFinalizationPollMuteSuppressesViaOriginPollId_D113()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-mute-organizer@example.test");
        Guid pollId = await SeedPollAsync(organizerId);
        Session mintedSession = await SeedSessionWithOrganizerAsync(organizerId);
        await SetOriginPollIdAsync(mintedSession.Id, PollId.From(pollId));

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            NotificationMute mute = NotificationMute.ForPoll(organizerId, PollId.From(pollId), _factory.Clock);
            mute.AttachToSession(mintedSession.Id);
            context.NotificationMutes.Add(mute);
            await context.SaveChangesAsync();
        }

        await SeedSubscriptionAsync(organizerId, "poll-mute-sub");
        Guid notificationId = await SeedPollNotificationAsync(
            organizerId, NotificationType.PollClosed, PollId.From(pollId));

        var sender = new FakePushSender();
        await RunJobAsync(sender);

        Assert.Equal(2, await ReadDispatchStateAsync(notificationId));
        Assert.Equal(0, sender.SendCount);
    }

    [Fact]
    public async Task GoneOutcomeRevokesTheSubscription_And_ServerErrorLeavesItIntactWithNoRetry()
    {
        UserId recipientId = await _factory.SeedUserAsync(email: "eviction@example.test");
        Session session = await SeedSessionWithOrganizerAsync(recipientId);
        Guid goneSubId = await SeedSubscriptionAsync(recipientId, "gone-sub");
        Guid failedSubId = await SeedSubscriptionAsync(recipientId, "failed-sub");
        Guid notificationId = await SeedNotificationAsync(recipientId, NotificationType.SessionConfirmed, session.Id);

        var sender = new FakePushSender(target => target.Endpoint.Contains("gone-sub", StringComparison.Ordinal)
            ? new PushSendResult(PushSendOutcome.Gone, 410)
            : new PushSendResult(PushSendOutcome.Failed, 500));

        await RunJobAsync(sender);

        Assert.Equal(1, await ReadDispatchStateAsync(notificationId));
        Assert.Equal(2, sender.SendCount);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext context = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        PartyUp.Infrastructure.Notifications.NotificationPushSend goneSend = await context.NotificationPushSends
            .AsNoTracking()
            .SingleAsync(s => s.PushSubscriptionId == PushSubscriptionId.From(goneSubId));
        Assert.Equal(PushSendOutcome.Gone, MapOutcome(goneSend.Outcome));

        PartyUp.Infrastructure.Notifications.NotificationPushSend failedSend = await context.NotificationPushSends
            .AsNoTracking()
            .SingleAsync(s => s.PushSubscriptionId == PushSubscriptionId.From(failedSubId));
        Assert.Equal(PushSendOutcome.Failed, MapOutcome(failedSend.Outcome));

        PushSubscription goneSubscription = await context.PushSubscriptions
            .AsNoTracking()
            .SingleAsync(s => s.Id == PushSubscriptionId.From(goneSubId));
        Assert.NotNull(goneSubscription.RevokedAt);

        PushSubscription failedSubscription = await context.PushSubscriptions
            .AsNoTracking()
            .SingleAsync(s => s.Id == PushSubscriptionId.From(failedSubId));
        Assert.Null(failedSubscription.RevokedAt);
    }

    /// <summary>The S1 verification carry: a notification can never claim against another user's subscription.</summary>
    [Fact]
    public async Task NeverClaimsAgainstAnotherUsersSubscription()
    {
        UserId recipientId = await _factory.SeedUserAsync(email: "carry-recipient@example.test");
        UserId otherUserId = await _factory.SeedUserAsync(email: "carry-other@example.test");
        Session session = await SeedSessionWithOrganizerAsync(recipientId);

        Guid recipientSubId = await SeedSubscriptionAsync(recipientId, "carry-recipient-sub");
        Guid otherUserSubId = await SeedSubscriptionAsync(otherUserId, "carry-other-sub");
        Guid notificationId = await SeedNotificationAsync(recipientId, NotificationType.SessionConfirmed, session.Id);

        var sender = new FakePushSender();
        await RunJobAsync(sender);

        Assert.Equal(1, sender.SendCount);
        Assert.Equal(1, await CountSendLogRowsAsync(notificationId));

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext context = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        bool claimedRecipientSub = await context.NotificationPushSends.AsNoTracking()
            .AnyAsync(s => s.PushSubscriptionId == PushSubscriptionId.From(recipientSubId));
        bool claimedOtherUserSub = await context.NotificationPushSends.AsNoTracking()
            .AnyAsync(s => s.PushSubscriptionId == PushSubscriptionId.From(otherUserSubId));
        Assert.True(claimedRecipientSub);
        Assert.False(claimedOtherUserSub);
    }

    /// <summary>
    /// The crash case: a row claimed but never outcome-written re-pages next tick, finds its send-log
    /// row already covers the only live subscription, and transitions to Dispatched without a second
    /// send.
    /// </summary>
    [Fact]
    public async Task ClaimedButUnwrittenRow_RepagesOnceThenReachesDispatched_WithNoSecondSend()
    {
        UserId recipientId = await _factory.SeedUserAsync(email: "crash@example.test");
        Session session = await SeedSessionWithOrganizerAsync(recipientId);
        Guid subscriptionId = await SeedSubscriptionAsync(recipientId, "crash-sub");
        Guid notificationId = await SeedNotificationAsync(recipientId, NotificationType.SessionConfirmed, session.Id);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var pushSends = scope.ServiceProvider.GetRequiredService<INotificationPushSendRepository>();
            Guid? claimed = await pushSends.ClaimAsync(
                NotificationId.From(notificationId), PushSubscriptionId.From(subscriptionId), _factory.Clock, default);
            Assert.NotNull(claimed);
        }

        var sender = new FakePushSender();
        await RunJobAsync(sender);

        Assert.Equal(0, sender.SendCount);
        Assert.Equal(1, await ReadDispatchStateAsync(notificationId));
        Assert.Equal(1, await CountSendLogRowsAsync(notificationId));
    }

    /// <summary>
    /// A rolling-deploy race: two workers legitimately reach the same notification. The unique
    /// constraint on <c>notification_push_sends</c> — not the Hangfire lock — is what guarantees exactly
    /// one send-log row per subscription and no double delivery.
    /// </summary>
    [Fact]
    public async Task ConcurrentWorkers_SendExactlyOncePerSubscription_D082Analogue()
    {
        UserId recipientId = await _factory.SeedUserAsync(email: "concurrent@example.test");
        Session session = await SeedSessionWithOrganizerAsync(recipientId);
        await SeedSubscriptionAsync(recipientId, "concurrent-sub");
        Guid notificationId = await SeedNotificationAsync(recipientId, NotificationType.SessionConfirmed, session.Id);

        var sharedSender = new FakePushSender();

        await using AsyncServiceScope scopeA = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope scopeB = _factory.Services.CreateAsyncScope();
        PushDispatchJob jobA = BuildJob(scopeA.ServiceProvider, sharedSender);
        PushDispatchJob jobB = BuildJob(scopeB.ServiceProvider, sharedSender);

        await Task.WhenAll(jobA.ExecuteAsync(default), jobB.ExecuteAsync(default));

        Assert.Equal(1, sharedSender.SendCount);
        Assert.Equal(1, await CountSendLogRowsAsync(notificationId));
        Assert.Equal(1, await ReadDispatchStateAsync(notificationId));
    }

    /// <summary>
    /// D-167: guests hold no channel at any layer. <c>push_subscriptions.user_id</c> has a hard FK to
    /// <c>users</c> (proven at the schema level by S1), so a guest identity — which is never a row in
    /// that table — cannot own a subscription; the fan-out repository this job's carry depends on is
    /// keyed on that same column and therefore structurally cannot reach one either.
    /// </summary>
    [Fact]
    public async Task NoPushPathCanReachAGuestCredential_D167()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "guest-host@example.test");
        Session session = await SeedSessionWithOrganizerAsync(organizerId);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var subscriptions = scope.ServiceProvider.GetRequiredService<IPushSubscriptionRepository>();

        // A guest's identity id is never a users.id row -- there is no registered-user UserId to look
        // subscriptions up by, which is the whole D-167 guarantee: the fan-out query this job's carry
        // relies on (IPushSubscriptionRepository.ListActiveForUserAsync) is keyed on UserId alone, and
        // no guest credential is ever one.
        UserId guestShapedId = UserId.From(Guid.CreateVersion7());
        IReadOnlyList<PushSubscriptionTarget> targets =
            await subscriptions.ListActiveForUserAsync(guestShapedId, default);
        Assert.Empty(targets);

        _ = session;
    }

    /// <summary>
    /// F1: a poison row (a null option-start reaching a template that requires it) must not abort the
    /// tick and block every row paged behind it -- the poison row is seeded first so it heads the
    /// <c>ORDER BY created_at, id</c> page, and the healthy row still has to dispatch in the same tick.
    /// </summary>
    [Fact]
    public async Task PoisonRowIsContained_AndDoesNotBlockAHealthyRowBehindIt_F1()
    {
        UserId poisonRecipientId = await _factory.SeedUserAsync(email: "poison@example.test");
        Guid pollId = await SeedPollAsync(poisonRecipientId);
        await SeedSubscriptionAsync(poisonRecipientId, "poison-sub");
        Guid poisonNotificationId = await SeedPollOptionSuggestedWithNullStartAsync(poisonRecipientId, pollId);

        _factory.Clock.Advance(Duration.FromMilliseconds(1));

        UserId healthyRecipientId = await _factory.SeedUserAsync(email: "healthy@example.test");
        Session session = await SeedSessionWithOrganizerAsync(healthyRecipientId);
        await SeedSubscriptionAsync(healthyRecipientId, "healthy-sub");
        Guid healthyNotificationId =
            await SeedNotificationAsync(healthyRecipientId, NotificationType.SessionConfirmed, session.Id);

        var sender = new FakePushSender();
        await RunJobAsync(sender);

        Assert.Equal(1, sender.SendCount);
        Assert.Equal(1, await ReadDispatchStateAsync(healthyNotificationId));
        Assert.Equal(1, await CountSendLogRowsAsync(healthyNotificationId));

        Assert.Equal(0, await ReadDispatchStateAsync(poisonNotificationId));
        Assert.Equal(0, await CountSendLogRowsAsync(poisonNotificationId));
    }

    private async Task<Guid> SeedPollOptionSuggestedWithNullStartAsync(UserId recipientId, Guid pollId)
    {
        Notification notification = Notification.Create(
            recipientId,
            NotificationType.PollOptionSuggested,
            NotificationSubject.ForPollOption(PollId.From(pollId), optionStartTime: null),
            _factory.Clock);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Notifications.Add(notification);
        await context.SaveChangesAsync();
        return notification.Id.Value;
    }

    private async Task RunJobAsync(FakePushSender sender)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        PushDispatchJob job = BuildJob(scope.ServiceProvider, sender);
        await job.ExecuteAsync(default);
    }

    private static PushDispatchJob BuildJob(IServiceProvider services, FakePushSender sender) =>
        BuildJobWithSender(services, sender);

    private static PushDispatchJob BuildJobWithSender(IServiceProvider services, IPushSender sender) =>
        new(
            services.GetRequiredService<INotificationDispatchRepository>(),
            services.GetRequiredService<INotificationRepository>(),
            services.GetRequiredService<INotificationPushSendRepository>(),
            services.GetRequiredService<IPushSubscriptionRepository>(),
            services.GetRequiredService<NotificationReadUseCases>(),
            new PushPayloadFactory(new EmbeddedPushTemplateRenderer()),
            sender,
            services.GetRequiredService<IClock>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PushDispatchJob>.Instance);

    private async Task<Session> SeedSessionWithOrganizerAsync(UserId organizerId, bool isPrivate = true)
    {
        SessionDetails details = new(
            SessionTitle.Create("Dispatch job test session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: (isPrivate ? PartyUp.Domain.Kernel.ContentVisibility.Private : PartyUp.Domain.Kernel.ContentVisibility.Public));
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        _ = session.DequeueEvents();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    private async Task SetOriginPollIdAsync(SessionId sessionId, PollId pollId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE sessions SET origin_poll_id = {pollId.Value} WHERE id = {sessionId.Value}
            """);
    }

    private async Task<Guid> SeedNotificationAsync(UserId recipientId, NotificationType type, SessionId sessionId)
    {
        Notification notification = Notification.Create(
            recipientId, type, NotificationSubject.ForSession(sessionId), _factory.Clock);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Notifications.Add(notification);
        await context.SaveChangesAsync();
        return notification.Id.Value;
    }

    private async Task<Guid> SeedPollNotificationAsync(UserId recipientId, NotificationType type, PollId pollId)
    {
        Notification notification = Notification.Create(
            recipientId, type, NotificationSubject.ForPoll(pollId), _factory.Clock);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Notifications.Add(notification);
        await context.SaveChangesAsync();
        return notification.Id.Value;
    }

    private async Task SeedSessionMuteAsync(UserId userId, SessionId sessionId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.NotificationMutes.Add(NotificationMute.ForSession(userId, sessionId, _factory.Clock));
        await context.SaveChangesAsync();
    }

    private async Task<Guid> SeedSubscriptionAsync(UserId userId, string endpointSuffix)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPushSubscriptionRepository>();
        PushSubscriptionId id = await repository.UpsertActiveAsync(
            userId,
            $"https://push.example/{endpointSuffix}",
            "public-key",
            "auth-secret",
            _factory.Clock,
            default);
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.SaveChangesAsync();
        return id.Value;
    }

    private async Task<Guid> SeedPollAsync(UserId organizerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid pollId = Guid.CreateVersion7();
        Instant deadline = _factory.Clock.GetCurrentInstant() + Duration.FromHours(48);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO polls
                (id, organizer_id, title, duration_minutes, platform, notes, min_attendance,
                 max_capacity, is_private, deadline_choice, deadline, status, created_at, selection_window_end)
            VALUES
                ({pollId}, {organizerId.Value}, 'Dispatch job mute test', 60, 3, '', 1, NULL, true, 2,
                 {deadline}, 0, {deadline - Duration.FromHours(72)}, {deadline + Duration.FromHours(48)})
            """);
        return pollId;
    }

    private async Task<short> ReadDispatchStateAsync(Guid notificationId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Notifications
            .AsNoTracking()
            .Where(n => n.Id == NotificationId.From(notificationId))
            .Select(n => EF.Property<short>(n, "DispatchState"))
            .SingleAsync();
    }

    private async Task<int> CountSendLogRowsAsync(Guid notificationId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.NotificationPushSends
            .AsNoTracking()
            .CountAsync(s => s.NotificationId == NotificationId.From(notificationId));
    }

    private static Application.Push.PushSendOutcome MapOutcome(PartyUp.Infrastructure.Notifications.PushSendOutcome outcome) =>
        outcome switch
        {
            PartyUp.Infrastructure.Notifications.PushSendOutcome.Delivered => Application.Push.PushSendOutcome.Delivered,
            PartyUp.Infrastructure.Notifications.PushSendOutcome.Gone => Application.Push.PushSendOutcome.Gone,
            PartyUp.Infrastructure.Notifications.PushSendOutcome.Failed => Application.Push.PushSendOutcome.Failed,
            _ => throw new InvalidOperationException($"Unexpected send-log outcome '{outcome}'."),
        };

    /// <summary>
    /// A transport substitute — never a mock of a repository or the read projection, both of which stay
    /// the real, DI-wired production adapters throughout this suite. The default behavior always
    /// succeeds; individual tests inject a per-target result to prove the eviction rule.
    /// </summary>
    private sealed class FakePushSender(Func<PushSubscriptionTarget, PushSendResult>? resolve = null) : IPushSender
    {
        private int _sendCount;

        public bool IsConfigured => true;

        public int SendCount => Volatile.Read(ref _sendCount);

        public Task<PushSendResult> SendAsync(
            PushSubscriptionTarget target,
            PushPayload payload,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            PushSendResult result = resolve?.Invoke(target) ?? new PushSendResult(PushSendOutcome.Delivered, 201);
            return Task.FromResult(result);
        }
    }
}
