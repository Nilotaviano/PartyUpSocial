using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Polls;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Application.Deletion;
using PartyUp.Application.Notifications;
using PartyUp.Application.Polls;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;
using PartyUp.Domain.Visibility;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Deletion;

/// <summary>
/// PHASE8_DESIGN.md §3 S4: one grace-expiry sweep over a maximally-seeded subject, asserting
/// DOMAIN_MODEL.md §11's whole declared effect list <em>together</em> rather than one surface per
/// test file. The sibling per-phase files (<c>Friends/DeletionSweepTests</c>,
/// <c>Sessions/SessionDeletionEffectsTests</c>, <c>Availability/AvailabilityDeletionEffectsTests</c>,
/// <c>Notifications/PushTransportDeletionPurgeTests</c>, <c>DeletionPurgeTests</c>,
/// <c>DeletionSweepTests</c>) each prove one step in isolation; what only a combined run can prove is
/// that the seven ordered steps still hold when every surface is populated at once — the retained
/// in-progress session next to the canceled not-started one, the actor-only notification surviving the
/// recipient purge, the avatar source's A-084 proxy token dying with its row.
///
/// <para>
/// The sweep is driven through <see cref="ExecuteDueDeletionsUseCase"/> — the same use case
/// <c>DeletionSweepHostedService</c> and the admin maintenance route call — so the A-068 lock protocol
/// in <see cref="ExecuteDeletionForUserUseCase"/> is exercised for real; <c>DeletionExecutor</c> is
/// never invoked directly.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeletionCompletenessE2ETests : IAsyncLifetime, IDisposable
{
    private readonly string _journalPath = Path.Combine(
        Path.GetTempPath(),
        "partyup-deletion-journal-" + Guid.NewGuid().ToString("N"));

    private readonly PartyUpApiFactory _factory;

    public DeletionCompletenessE2ETests() =>
        _factory = new PartyUpApiFactory(deletionJournalPath: _journalPath);

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_journalPath))
        {
            Directory.Delete(_journalPath, recursive: true);
        }
    }

    /// <summary>
    /// The whole §11 effect list over one subject: tombstone + PII null + FK-stable row, the D-145
    /// ownerless username reservation, owned not-started session canceled while the owned in-progress
    /// one runs on, the owned poll expired with its D-170 notification, participation and join-request
    /// outcomes on other people's content, reminders canceled, friend graph and blocks gone in both
    /// directions, the A-075 availability cascade, the push-transport purge ahead of the notifications
    /// purge, recipient rows gone against actor-only rows preserved and masked, avatar sources gone with
    /// their A-084 proxy tokens, credentials purged, and the journal row appended and shipped to NDJSON.
    /// </summary>
    [Fact]
    public async Task GraceExpirySweep_ProducesEveryDeclaredEffect_D092()
    {
        DeletionCompletenessSeed seed = await SeedAsync();
        _factory.Clock.Advance(Duration.FromHours(84));
        Instant executionInstant = _factory.Clock.GetCurrentInstant();

        DeletionSweepResult sweep = await RunSweepAsync();

        Assert.Equal(1, sweep.Executed);
        Assert.Equal(0, sweep.Failed);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();

        // 1. Tombstone, PII nulled, and the row itself still there (FK-stable, A-017).
        User subject = await verify.DomainUsers.AsNoTracking()
            .Include(user => user.PlatformLinks)
            .SingleAsync(user => user.Id == seed.SubjectId);
        Assert.True(subject.IsTombstoned);
        Assert.Null(subject.Username);
        Assert.Null(subject.DisplayName);
        Assert.Null(subject.Avatar);
        Assert.Null(subject.FriendCode);
        Assert.Null(subject.GraceEndsAt);
        Assert.Equal(executionInstant, subject.DeletedAt);
        Assert.Empty(subject.PlatformLinks);
        // PHASE8_DESIGN.md §2 Q1: last_seen_at is PII-adjacent telemetry and a tombstone must never
        // keep counting as an active user, so deletion nulls it with the rest.
        Assert.Null(await verify.DomainUsers.AsNoTracking()
            .Where(user => user.Id == seed.SubjectId)
            .Select(user => EF.Property<Instant?>(user, UserActivityWriter.LastSeenProperty))
            .SingleAsync());

        // 2. D-145: the username enters a thirty-day, owner-less reservation.
        UsernameRegistryEntry username = await verify.UsernameRegistry.AsNoTracking()
            .SingleAsync(entry => entry.NameCanonical == seed.SubjectCanonicalUsername);
        Assert.True(username.IsReserved);
        Assert.Null(username.OwnerId);
        Assert.Equal(executionInstant + UsernameRegistry.ReservationPeriod, username.ReservedUntil);

        // 3. §11 step 1: owned not-started canceled, owned in-progress runs to completion (§12-35).
        Assert.Equal(
            SessionStatus.Canceled,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == seed.OwnedNotStartedSessionId)).Status);
        Assert.Equal(
            SessionStatus.Confirmed,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == seed.OwnedInProgressSessionId)).Status);

        // 4. §11 step 2 / D-170: the owned poll expires and its invitee — never the deleting owner —
        //    gets the PollExpired row.
        Assert.Equal(
            PollStatus.Expired,
            (await verify.Polls.AsNoTracking().SingleAsync(p => p.Id == seed.OwnedPollId)).Status);
        Assert.Equal(
            1,
            await verify.Notifications.CountAsync(n =>
                n.PollId == seed.OwnedPollId &&
                n.Type == NotificationType.PollExpired &&
                n.RecipientUserId == seed.FriendAcceptedId));
        // No separate "PollExpired excludes the subject" assertion here: assertion 12 below already
        // proves zero notifications of any type have RecipientUserId == seed.SubjectId post-purge,
        // which subsumes this one — a subject-recipient PollExpired row would already fail there.

        // 5. The counterpart notification from the owned cancellation, materialized excluding the subject.
        Assert.Equal(
            1,
            await verify.Notifications.CountAsync(n =>
                n.SessionId == seed.OwnedNotStartedSessionId &&
                n.Type == NotificationType.SessionCanceled &&
                n.RecipientUserId == seed.FriendAcceptedId));
        // Same reasoning as above: subsumed by assertion 12's blanket subject-recipient-count check.

        // 6. §11 step 3: removal from other people's not-started content, retention in in-progress content.
        Assert.True((await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == seed.ParticipatedFutureSessionId && p.UserId == seed.SubjectId))
            .IsRemoved);
        Assert.False((await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == seed.ParticipatedInProgressSessionId && p.UserId == seed.SubjectId))
            .IsRemoved);
        Assert.True((await verify.PollParticipants.AsNoTracking()
            .SingleAsync(p => p.PollId == seed.ParticipatedPollId && p.UserId == seed.SubjectId))
            .IsRemoved);

        // 7. The subject's own open join requests lapse unconditionally; a third party's request lapses
        //    only where the domain lapses it — through the owned session's cancel transition — and
        //    stays Pending on the retained in-progress session.
        Assert.Equal(
            JoinRequestStatus.Lapsed,
            (await verify.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == seed.OwnSessionJoinRequestId)).Status);
        Assert.Equal(
            JoinRequestStatus.Lapsed,
            (await verify.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == seed.OwnPollJoinRequestId)).Status);
        Assert.Equal(
            JoinRequestStatus.Lapsed,
            (await verify.JoinRequests.AsNoTracking()
                .SingleAsync(r => r.Id == seed.ThirdPartyRequestOnCanceledSessionId)).Status);
        Assert.Equal(
            JoinRequestStatus.Pending,
            (await verify.JoinRequests.AsNoTracking()
                .SingleAsync(r => r.Id == seed.ThirdPartyRequestOnRetainedSessionId)).Status);

        // 8. §9: the pending reminder behind the removed participation is canceled.
        Assert.Equal(
            SessionReminderHandoffState.Canceled,
            (await verify.SessionReminderHandoffs.AsNoTracking()
                .SingleAsync(h => h.Id == seed.ReminderHandoffId)).State);
        Assert.Equal(
            0,
            await verify.SessionReminderHandoffs.CountAsync(h =>
                h.RecipientUserId == seed.SubjectId && h.State != SessionReminderHandoffState.Canceled));

        // 9. §11 step 4: friendships and blocks gone in both directions, control rows untouched.
        Assert.Equal(
            0,
            await verify.Friendships.CountAsync(f =>
                f.UserLoId == seed.SubjectId || f.UserHiId == seed.SubjectId));
        Assert.Equal(
            0,
            await verify.Blocks.CountAsync(b =>
                b.BlockerId == seed.SubjectId || b.BlockedId == seed.SubjectId));
        Assert.True(await verify.Friendships.AnyAsync(f => f.Id == seed.ControlFriendshipId));
        Assert.True(await verify.Blocks.AnyAsync(b => b.Id == seed.ControlBlockId));

        // 10. §11 step 5 / A-075: the whole availability surface cascades away.
        Assert.False(await verify.AvailabilitySeries.AsNoTracking().AnyAsync(s => s.Id == seed.SeriesId));
        Assert.Equal(
            0,
            await verify.AvailabilityBlocks.CountAsync(b =>
                b.Id == seed.MaterializedBlockId || b.Id == seed.StandaloneBlockId));
        Assert.False(await verify.AvailabilityOccurrenceSkips.AsNoTracking()
            .AnyAsync(s => s.SeriesId == seed.SeriesId));

        // 11. The push transport purge lands ahead of the notifications purge in the same transaction.
        Assert.Equal(0, await verify.PushSubscriptions.CountAsync(s => s.UserId == seed.SubjectId));
        Assert.Equal(
            0,
            await verify.NotificationPushSends.CountAsync(s => s.PushSubscriptionId == seed.PushSubscriptionId));
        Assert.Equal(0, await verify.NotificationMutes.CountAsync(m => m.UserId == seed.SubjectId));

        // 12. Recipient rows gone; the actor-only row survives and renders "Anonymous User".
        Assert.Equal(0, await verify.Notifications.CountAsync(n => n.RecipientUserId == seed.SubjectId));
        Notification actorOnly = await verify.Notifications.AsNoTracking()
            .SingleAsync(n => n.Id == seed.ActorOnlyNotificationId);
        Assert.Equal(seed.SubjectId, actorOnly.ActorUserId);
        Assert.True(await verify.Notifications.AnyAsync(n => n.Id == seed.ControlNotificationId));

        // Drive the masking check through the same read path a client actually calls (S2's
        // NotificationReadUseCases projector), not IdentityMasking.ForUser directly, so this proves
        // the read path renders it, not just that the domain rule exists in isolation.
        SessionPage<NotificationReadModel> friendPendingNotifications = await verification.ServiceProvider
            .GetRequiredService<ListNotificationsUseCase>()
            .ExecuteAsync(seed.FriendPendingId, unreadOnly: false, cursor: null, pageSize: 50, default);
        NotificationReadModel actorOnlyRow = friendPendingNotifications.Items
            .Single(row => row.Id == seed.ActorOnlyNotificationId);
        Assert.True(actorOnlyRow.Actor is { IsAnonymous: true });
        Assert.Equal(MaskingReason.Tombstoned, actorOnlyRow.Actor!.Value.Reason);

        // 13. Avatar sources gone, including the A-084 plaintext proxy-token row whose only revocation
        //     authority is that row's deletion.
        Assert.Equal(0, await verify.AvatarSources.CountAsync(source => source.UserId == seed.SubjectId));
        Assert.False(await verify.AvatarSources.AsNoTracking()
            .AnyAsync(source => source.Token == seed.AvatarProxyToken));

        // 14. Credential purge: email, password, external logins and issued auth tokens.
        IUserAccountGateway accounts = verification.ServiceProvider.GetRequiredService<IUserAccountGateway>();
        Assert.Null(await accounts.GetEmailAsync(seed.SubjectId, default));
        Assert.False(await accounts.HasPasswordAsync(seed.SubjectId, default));
        Assert.Empty(await accounts.ListLoginProvidersAsync(seed.SubjectId, default));
        Assert.Null(await accounts.FindUserIdByLoginAsync(
            ExternalProviderPolicy.Google, seed.GoogleProviderKey, default));
        TokenRedemption redemption = await verification.ServiceProvider
            .GetRequiredService<ISecurityTokenService>()
            .RedeemAsync(new RedeemTokenRequest(AuthTokenPurpose.PasswordReset, seed.RawAuthToken), default);
        Assert.False(redemption.Succeeded);

        // 15. A-017: the journal row is appended and shipped to the NDJSON drop.
        Assert.Contains(
            await verification.ServiceProvider.GetRequiredService<IDeletionJournal>()
                .ReadSinceAsync(Instant.MinValue, 100, default),
            entry => entry.UserId == seed.SubjectId);
        Assert.All(
            await verify.DeletionJournal.AsNoTracking()
                .Where(record => record.UserId == seed.SubjectId)
                .ToListAsync(),
            record => Assert.NotNull(record.ShippedAt));
        Assert.Contains(
            ShippedJournalLines(),
            line => line.Contains(seed.SubjectId.Value.ToString("D"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// D-148 / §12-34: before <c>GraceEndsAt</c> the sweep finds nothing due and the domain is
    /// untouched — the same maximal seed, one instant short of expiry, changes not a single row.
    /// </summary>
    [Fact]
    public async Task BeforeGraceExpiry_TheSweepIsInert_D148()
    {
        DeletionCompletenessSeed seed = await SeedAsync();

        DeletionSweepResult sweep = await RunSweepAsync();

        Assert.Equal(0, sweep.Executed);
        Assert.Equal(0, sweep.Failed);
        await using AsyncServiceScope directScope = _factory.Services.CreateAsyncScope();
        Assert.False(await directScope.ServiceProvider
            .GetRequiredService<ExecuteDeletionForUserUseCase>()
            .ExecuteAsync(seed.SubjectId, default));

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        User subject = await verify.DomainUsers.AsNoTracking().SingleAsync(user => user.Id == seed.SubjectId);
        Assert.True(subject.IsPendingDeletion);
        Assert.False(subject.IsTombstoned);
        Assert.Equal(UsernameRegistryState.Active, (await verify.UsernameRegistry.AsNoTracking()
            .SingleAsync(entry => entry.NameCanonical == seed.SubjectCanonicalUsername)).State);
        Assert.Equal(
            SessionStatus.Confirmed,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == seed.OwnedNotStartedSessionId)).Status);
        Assert.Equal(
            PollStatus.Polling,
            (await verify.Polls.AsNoTracking().SingleAsync(p => p.Id == seed.OwnedPollId)).Status);
        Assert.False((await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == seed.ParticipatedFutureSessionId && p.UserId == seed.SubjectId))
            .IsRemoved);
        Assert.False((await verify.PollParticipants.AsNoTracking()
            .SingleAsync(p => p.PollId == seed.ParticipatedPollId && p.UserId == seed.SubjectId))
            .IsRemoved);
        Assert.Equal(
            JoinRequestStatus.Pending,
            (await verify.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == seed.OwnSessionJoinRequestId)).Status);
        Assert.Equal(
            JoinRequestStatus.Pending,
            (await verify.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == seed.OwnPollJoinRequestId)).Status);
        Assert.Equal(
            SessionReminderHandoffState.Pending,
            (await verify.SessionReminderHandoffs.AsNoTracking()
                .SingleAsync(h => h.Id == seed.ReminderHandoffId)).State);
        Assert.Equal(
            4,
            await verify.Friendships.CountAsync(f =>
                f.UserLoId == seed.SubjectId || f.UserHiId == seed.SubjectId));
        Assert.Equal(
            2,
            await verify.Blocks.CountAsync(b =>
                b.BlockerId == seed.SubjectId || b.BlockedId == seed.SubjectId));
        Assert.True(await verify.AvailabilitySeries.AsNoTracking().AnyAsync(s => s.Id == seed.SeriesId));
        Assert.Equal(1, await verify.PushSubscriptions.CountAsync(s => s.UserId == seed.SubjectId));
        Assert.Equal(2, await verify.NotificationMutes.CountAsync(m => m.UserId == seed.SubjectId));
        Assert.Equal(1, await verify.AvatarSources.CountAsync(source => source.UserId == seed.SubjectId));
        Assert.NotNull(await verification.ServiceProvider.GetRequiredService<IUserAccountGateway>()
            .GetEmailAsync(seed.SubjectId, default));
        Assert.DoesNotContain(
            await verification.ServiceProvider.GetRequiredService<IDeletionJournal>()
                .ReadSinceAsync(Instant.MinValue, 100, default),
            entry => entry.UserId == seed.SubjectId);
        Assert.Empty(ShippedJournalLines());
    }

    private IReadOnlyList<string> ShippedJournalLines() =>
        Directory.Exists(_journalPath)
            ? [.. Directory.EnumerateFiles(_journalPath, "*.ndjson").SelectMany(File.ReadAllLines)]
            : [];

    private async Task<DeletionSweepResult> RunSweepAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ExecuteDueDeletionsUseCase>()
            .ExecuteAsync(batchSize: 50, default);
    }

    /// <summary>
    /// Seeds every surface §11 touches, then advances to 84 hours before grace expiry. The 84-hour
    /// offset is the shipped <c>DeletionSweepTests</c> poll idiom: §11 step 3 skips a poll whose 48h
    /// selection window has logically ended, and D-070's longest deadline (48h) plus that window is
    /// only 96h — shorter than the 7-day grace — so the polls and sessions are created only after most
    /// of the grace period has already elapsed. The caller advances the remaining 84 hours (or does
    /// not, for the D-148 inertness case).
    /// </summary>
    private async Task<DeletionCompletenessSeed> SeedAsync()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string subjectEmail = $"completeness-subject-{suffix}@example.test";
        string googleKey = $"google-completeness-{suffix}";
        // The subject starts Active: an avatar source can only be captured while the profile is still
        // mutable, so deletion is requested below, after every pre-deletion surface exists.
        UserId subjectId = await _factory.SeedUserAsync(
            email: subjectEmail, password: "Completeness-Password-42!");
        UserId friendPendingId = await SeedCounterpartAsync("friend-pending", suffix);
        UserId friendAcceptedId = await SeedCounterpartAsync("friend-accepted", suffix);
        UserId blockedBySubjectId = await SeedCounterpartAsync("blocked", suffix);
        UserId blockerOfSubjectId = await SeedCounterpartAsync("blocker", suffix);
        UserId controlId = await SeedCounterpartAsync("control", suffix);
        UserId sessionHostId = await SeedCounterpartAsync("session-host", suffix);
        UserId inProgressHostId = await SeedCounterpartAsync("inprogress-host", suffix);
        UserId sessionRequestHostId = await SeedCounterpartAsync("session-req-host", suffix);
        UserId pollHostId = await SeedCounterpartAsync("poll-host", suffix);
        UserId pollRequestHostId = await SeedCounterpartAsync("poll-req-host", suffix);
        UserId pollRequestHostFriendId = await SeedCounterpartAsync("poll-req-friend", suffix);
        UserId thirdPartyRequesterId = await SeedCounterpartAsync("third-party", suffix);

        string subjectCanonicalUsername;
        FriendshipId controlFriendshipId;
        BlockId controlBlockId;
        NotificationId recipientNotificationId;
        NotificationId actorOnlyNotificationId;
        NotificationId controlNotificationId;
        AvailabilitySeriesId seriesId;
        AvailabilityBlockId materializedBlockId;
        AvailabilityBlockId standaloneBlockId;
        string avatarProxyToken;

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User subject = await context.DomainUsers.SingleAsync(user => user.Id == subjectId);
            User friendPending = await context.DomainUsers.SingleAsync(user => user.Id == friendPendingId);
            User blocked = await context.DomainUsers.SingleAsync(user => user.Id == blockedBySubjectId);
            User blocker = await context.DomainUsers.SingleAsync(user => user.Id == blockerOfSubjectId);
            User control = await context.DomainUsers.SingleAsync(user => user.Id == controlId);

            UsernameRegistryEntry username = UsernameRegistryEntry.ClaimNew(subject.Username!.Value, subject.Id);
            subjectCanonicalUsername = username.NameCanonical;
            context.UsernameRegistry.Add(username);

            Friendship pendingFriendship = Friendship.Request(subject, friendPending, _factory.Clock);
            _ = pendingFriendship.DequeueEvents();
            context.Friendships.Add(pendingFriendship);
            await PollTestHelpers.SeedAcceptedFriendshipAsync(
                context, subjectId, friendAcceptedId, _factory.Clock);
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, pollHostId, subjectId, _factory.Clock);
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, pollRequestHostId, subjectId, _factory.Clock);
            await PollTestHelpers.SeedAcceptedFriendshipAsync(
                context, pollRequestHostId, pollRequestHostFriendId, _factory.Clock);

            Friendship controlFriendship = Friendship.Request(friendPending, control, _factory.Clock);
            controlFriendship.Accept(control.Id, _factory.Clock);
            _ = controlFriendship.DequeueEvents();
            controlFriendshipId = controlFriendship.Id;
            context.Friendships.Add(controlFriendship);

            Block outbound = Block.Create(subject.Id, blocked.Id, blocked.Username!.Value, _factory.Clock);
            Block inbound = Block.Create(blocker.Id, subject.Id, subject.Username!.Value, _factory.Clock);
            Block controlBlock = Block.Create(
                friendPending.Id, control.Id, control.Username!.Value, _factory.Clock);
            controlBlockId = controlBlock.Id;
            context.Blocks.AddRange(outbound, inbound, controlBlock);

            Notification recipient = Notification.Create(
                subject.Id,
                NotificationType.FriendRequestReceived,
                NotificationSubject.ForActor(friendPending.Id),
                _factory.Clock);
            Notification actorOnly = Notification.Create(
                friendPending.Id,
                NotificationType.FriendRequestReceived,
                NotificationSubject.ForActor(subject.Id),
                _factory.Clock);
            Notification controlNotification = Notification.Create(
                control.Id,
                NotificationType.FriendRequestReceived,
                NotificationSubject.ForActor(friendPending.Id),
                _factory.Clock);
            recipientNotificationId = recipient.Id;
            actorOnlyNotificationId = actorOnly.Id;
            controlNotificationId = controlNotification.Id;
            context.Notifications.AddRange(recipient, actorOnly, controlNotification);

            AvatarSource avatarSource = subject.CaptureAvatarSource(
                AvatarSourceKind.SteamPlatform, [1, 2, 3, 4], AvatarSource.AllowedContentType, _factory.Clock);
            avatarProxyToken = avatarSource.Token;

            // D-032/D-127: a real platform link so :101's Assert.Empty(subject.PlatformLinks) can
            // actually fail on a regression, instead of vacuously passing on an always-empty list.
            subject.LinkPlatform(Platform.PlayStation, GamingHandle.Create("completeness-handle"));

            DateTimeZone zone = DateTimeZoneProviders.Tzdb["America/Sao_Paulo"];
            AvailabilitySeries series = AvailabilitySeries.Create(
                subjectId,
                WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60)),
                zone,
                new LocalDate(2026, 9, 1));
            seriesId = series.Id;
            LocalDate materializedDate = new(2026, 9, 8);
            Instant materializedStart = materializedDate.AtStartOfDayInZone(zone).ToInstant();
            AvailabilityBlock materializedBlock = AvailabilityBlock.Materialize(
                series,
                materializedDate,
                new TimeRange(materializedStart, materializedStart + Duration.FromHours(1)));
            materializedBlockId = materializedBlock.Id;
            Instant standaloneStart = Instant.FromUtc(2026, 9, 20, 10, 0);
            AvailabilityBlock standaloneBlock = AvailabilityBlock.Create(
                subjectId, new TimeRange(standaloneStart, standaloneStart + Duration.FromMinutes(30)));
            standaloneBlockId = standaloneBlock.Id;
            context.AvailabilitySeries.Add(series);
            context.AvailabilityBlocks.AddRange(materializedBlock, standaloneBlock);
            context.AvailabilityOccurrenceSkips.Add(
                AvailabilityOccurrenceSkip.For(series, materializedDate));

            subject.RequestDeletion(_factory.Clock);
            _ = subject.DequeueEvents();

            await context.SaveChangesAsync();
        }

        PushSubscriptionId pushSubscriptionId;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            pushSubscriptionId = await scope.ServiceProvider.GetRequiredService<IPushSubscriptionRepository>()
                .UpsertActiveAsync(
                    subjectId,
                    $"https://push.example/completeness-{suffix}",
                    "public-key",
                    "auth-secret",
                    _factory.Clock,
                    default);
            await context.SaveChangesAsync();

            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<INotificationPushSendRepository>()
                .ClaimAsync(recipientNotificationId, pushSubscriptionId, _factory.Clock, default));

            // Neither notification_mutes column carries an FK (a shipped gap pre-dating Phase 7), so
            // synthetic ids exercise the purge without minting extra sessions/polls.
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_mutes (id, user_id, session_id, created_at)
                VALUES ({Guid.CreateVersion7()}, {subjectId.Value}, {Guid.CreateVersion7()}, now())
                """);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_mutes (id, user_id, poll_id, created_at)
                VALUES ({Guid.CreateVersion7()}, {subjectId.Value}, {Guid.CreateVersion7()}, now())
                """);

            // PHASE8_DESIGN.md §2 Q1: the subject has been seen, so the tombstone assertion below can
            // actually fail if deletion ever stops clearing the activity column.
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE users SET last_seen_at = {_factory.Clock.GetCurrentInstant()}
                WHERE id = {subjectId.Value}
                """);
        }

        string rawAuthToken;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAccountGateway>()
                .AddLoginAsync(subjectId, ExternalProviderPolicy.Google, googleKey, default);
            IssuedToken issued = await scope.ServiceProvider.GetRequiredService<ISecurityTokenService>()
                .IssueAsync(
                    new IssueTokenRequest(
                        AuthTokenPurpose.PasswordReset,
                        subjectId,
                        subjectEmail.ToUpperInvariant(),
                        Duration.FromDays(30),
                        Email: subjectEmail),
                    default);
            rawAuthToken = issued.RawToken;
        }

        _factory.Clock.Advance(User.DeletionGracePeriod - Duration.FromHours(84));

        SessionId ownedNotStartedId;
        SessionId ownedInProgressId;
        SessionId participatedFutureId;
        SessionId participatedInProgressId;
        JoinRequestId ownSessionJoinRequestId;
        JoinRequestId thirdPartyOnCanceledId;
        JoinRequestId thirdPartyOnRetainedId;
        Guid reminderHandoffId = Guid.NewGuid();

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // "Future" and "in progress" are evaluated at the execution instant, 84 hours from here.
            const int futureLeadMinutes = (84 * 60) + 180;
            const int inProgressLeadMinutes = (84 * 60) - 10;

            Session ownedNotStarted = Session.Create(
                subjectId, DetailsFor("Owned not-started", futureLeadMinutes), [], _factory.Clock);
            ownedNotStarted.Invite(friendAcceptedId, _factory.Clock);
            ownedNotStarted.Rsvp(
                ParticipantRef.ForUser(friendAcceptedId), ParticipationStatus.Going, _factory.Clock);
            thirdPartyOnCanceledId = ownedNotStarted.OpenJoinRequest(thirdPartyRequesterId, _factory.Clock).Id;
            _ = ownedNotStarted.DequeueEvents();
            ownedNotStartedId = ownedNotStarted.Id;

            Session ownedInProgress = Session.Create(
                subjectId,
                DetailsFor("Owned in-progress", inProgressLeadMinutes, durationMinutes: 120),
                [],
                _factory.Clock);
            thirdPartyOnRetainedId = ownedInProgress.OpenJoinRequest(thirdPartyRequesterId, _factory.Clock).Id;
            _ = ownedInProgress.DequeueEvents();
            ownedInProgressId = ownedInProgress.Id;

            Session participatedFuture = Session.Create(
                sessionHostId, DetailsFor("Other's not-started", futureLeadMinutes), [], _factory.Clock);
            participatedFuture.Invite(subjectId, _factory.Clock);
            participatedFuture.Rsvp(ParticipantRef.ForUser(subjectId), ParticipationStatus.Going, _factory.Clock);
            _ = participatedFuture.DequeueEvents();
            participatedFutureId = participatedFuture.Id;

            Session participatedInProgress = Session.Create(
                inProgressHostId,
                DetailsFor("Other's in-progress", inProgressLeadMinutes, durationMinutes: 120),
                [],
                _factory.Clock);
            participatedInProgress.Invite(subjectId, _factory.Clock);
            participatedInProgress.Rsvp(
                ParticipantRef.ForUser(subjectId), ParticipationStatus.Going, _factory.Clock);
            _ = participatedInProgress.DequeueEvents();
            participatedInProgressId = participatedInProgress.Id;

            Session requestTarget = Session.Create(
                sessionRequestHostId, DetailsFor("Other's request target", futureLeadMinutes), [], _factory.Clock);
            ownSessionJoinRequestId = requestTarget.OpenJoinRequest(subjectId, _factory.Clock).Id;
            _ = requestTarget.DequeueEvents();

            context.Sessions.AddRange(
                ownedNotStarted,
                ownedInProgress,
                participatedFuture,
                participatedInProgress,
                requestTarget);
            context.SessionReminderHandoffs.Add(new SessionReminderHandoff
            {
                Id = reminderHandoffId,
                SessionId = participatedFutureId,
                RecipientUserId = subjectId,
                DueAt = _factory.Clock.GetCurrentInstant() + Duration.FromHours(84),
                State = SessionReminderHandoffState.Pending,
                PlannedAt = _factory.Clock.GetCurrentInstant(),
                DeliveredAt = null,
                Version = 0,
                Attempts = 0,
                LastAttemptAt = null,
            });
            await context.SaveChangesAsync();
        }

        PollId ownedPollId;
        PollId participatedPollId;
        JoinRequestId ownPollJoinRequestId;

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Instant now = _factory.Clock.GetCurrentInstant();
            ownedPollId = await CreatePollAsync(scope, subjectId, "Owned poll", friendAcceptedId, now);
            participatedPollId = await CreatePollAsync(scope, pollHostId, "Other's poll", subjectId, now);
            PollId requestedPollId = await CreatePollAsync(
                scope, pollRequestHostId, "Other's request-target poll", pollRequestHostFriendId, now);

            PollJoinRequestCommandResult joined = await PollTestHelpers.BuildJoinRequestUseCases(scope)
                .RequestJoinAsync(requestedPollId, subjectId, default);
            Assert.True(joined.Succeeded, joined.Code);
            ownPollJoinRequestId = joined.JoinRequest!.Id;
        }

        return new DeletionCompletenessSeed(
            subjectId,
            friendPendingId,
            friendAcceptedId,
            subjectCanonicalUsername,
            controlFriendshipId,
            controlBlockId,
            actorOnlyNotificationId,
            controlNotificationId,
            ownedNotStartedId,
            ownedInProgressId,
            participatedFutureId,
            participatedInProgressId,
            ownSessionJoinRequestId,
            thirdPartyOnCanceledId,
            thirdPartyOnRetainedId,
            reminderHandoffId,
            ownedPollId,
            participatedPollId,
            ownPollJoinRequestId,
            seriesId,
            materializedBlockId,
            standaloneBlockId,
            pushSubscriptionId,
            avatarProxyToken,
            googleKey,
            rawAuthToken);
    }

    private static async Task<PollId> CreatePollAsync(
        AsyncServiceScope scope,
        UserId organizerId,
        string title,
        UserId inviteeId,
        Instant now)
    {
        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                title,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                Options: [new CreatePollOptionInput(now + Duration.FromDays(6), 60), new CreatePollOptionInput(now + Duration.FromDays(7), 60)],
                DeadlineChoice: PollDeadlineChoice.Hours48,
                InviteeUserIds: [inviteeId],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);
        return created.Poll!.Id;
    }

    private Task<UserId> SeedCounterpartAsync(string role, string suffix) =>
        _factory.SeedUserAsync(email: $"completeness-{role}-{suffix}@example.test");

    private SessionDetails DetailsFor(string title, int leadMinutes, int durationMinutes = 60) => new(
        SessionTitle.Create(title),
        _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(leadMinutes),
        Duration15.FromMinutes(durationMinutes),
        Platform.NotApplicable,
        null,
        SessionNotes.Empty,
        minAttendance: 1,
        maxCapacity: null,
        visibility: ContentVisibility.Private);
}

internal sealed record DeletionCompletenessSeed(
    UserId SubjectId,
    UserId FriendPendingId,
    UserId FriendAcceptedId,
    string SubjectCanonicalUsername,
    FriendshipId ControlFriendshipId,
    BlockId ControlBlockId,
    NotificationId ActorOnlyNotificationId,
    NotificationId ControlNotificationId,
    SessionId OwnedNotStartedSessionId,
    SessionId OwnedInProgressSessionId,
    SessionId ParticipatedFutureSessionId,
    SessionId ParticipatedInProgressSessionId,
    JoinRequestId OwnSessionJoinRequestId,
    JoinRequestId ThirdPartyRequestOnCanceledSessionId,
    JoinRequestId ThirdPartyRequestOnRetainedSessionId,
    Guid ReminderHandoffId,
    PollId OwnedPollId,
    PollId ParticipatedPollId,
    JoinRequestId OwnPollJoinRequestId,
    AvailabilitySeriesId SeriesId,
    AvailabilityBlockId MaterializedBlockId,
    AvailabilityBlockId StandaloneBlockId,
    PushSubscriptionId PushSubscriptionId,
    string AvatarProxyToken,
    string GoogleProviderKey,
    string RawAuthToken);
