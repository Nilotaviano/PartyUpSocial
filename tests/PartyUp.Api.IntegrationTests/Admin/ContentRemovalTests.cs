using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Polls;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Admin;
using PartyUp.Application.Deletion;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Admin;

/// <summary>
/// D-180's optional content removal end to end (PHASE8_DESIGN.md §3 S2): one admin action bans the
/// subject and runs DOMAIN_MODEL §11 steps 1–3 over a real graph, in one transaction through the
/// shared A-068 lock envelope. The exclusions are asserted as absences — the friend graph, the
/// username, the account's PII, its lifecycle, its activity column and its Completed/in-progress
/// history all survive, no journal row appears, and an unban restores none of the removed content.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ContentRemovalTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RemoveContent_ProducesTheFiveEffectsAndNothingElse_D180()
    {
        RemovalSeed seed = await SeedAsync();

        AdminUserMutationResult result = await RemoveContentAsync(seed.SubjectId);

        Assert.True(result.Succeeded, result.Code);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();

        // Effect 1: the subject's not-started owned session is canceled; the in-progress one is not.
        Assert.Equal(
            SessionStatus.Canceled,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == seed.OwnedFutureId)).Status);
        Assert.NotEqual(
            SessionStatus.Canceled,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == seed.OwnedInProgressId)).Status);

        // Excluded: an owned session that already reached Completed before the ban is history, not
        // live content — DOMAIN_MODEL §11 steps 1–3 only ever touch not-started/live content.
        Assert.Equal(
            SessionStatus.Completed,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == seed.OwnedCompletedId)).Status);

        // Effect 2: the owned live poll expires.
        Assert.Equal(
            PollStatus.Expired,
            (await verify.Polls.AsNoTracking().SingleAsync(p => p.Id == seed.OwnedPollId)).Status);

        // Effect 3: future participation on someone else's session is removed.
        Assert.True((await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == seed.ParticipatedFutureId && p.UserId == seed.SubjectId))
            .IsRemoved);

        // Effect 4: the subject's own pending join request lapses.
        Assert.Equal(
            JoinRequestStatus.Lapsed,
            (await verify.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == seed.JoinRequestId)).Status);

        // Effect 5: the reminder that removal orphaned is canceled.
        Assert.Equal(0, await verify.SessionReminderHandoffs.CountAsync(h =>
            h.SessionId == seed.ParticipatedFutureId && h.RecipientUserId == seed.SubjectId &&
            h.State != SessionReminderHandoffState.Canceled));

        // The ban itself, and only the ban, on the account.
        User subject = await verify.DomainUsers.AsNoTracking().SingleAsync(u => u.Id == seed.SubjectId);
        Assert.NotNull(subject.BannedAt);
        Assert.False(subject.IsTombstoned);
        Assert.Equal(UserLifecycle.Active, subject.Lifecycle);
        Assert.NotNull(subject.Username);
        Assert.NotNull(subject.DisplayName);
        Assert.NotNull(subject.FriendCode);

        // Excluded: friendships survive, the username is never released, no journal row is written,
        // and last_seen_at is untouched (a banned account is still a live account, unlike a tombstone).
        Assert.Equal(
            1,
            await verify.Friendships.CountAsync(f =>
                f.UserLoId == seed.SubjectId || f.UserHiId == seed.SubjectId));
        Assert.Empty(await verify.UsernameRegistry.AsNoTracking()
            .Where(entry => entry.OwnerId == null)
            .ToListAsync());
        Assert.DoesNotContain(
            await verification.ServiceProvider.GetRequiredService<IDeletionJournal>()
                .ReadSinceAsync(Instant.MinValue, 100, default),
            entry => entry.UserId == seed.SubjectId);
        Assert.Equal(seed.SeenAt, await LastSeenAsync(verify, seed.SubjectId));
        Assert.NotNull(await verification.ServiceProvider.GetRequiredService<IUserAccountGateway>()
            .GetEmailAsync(seed.SubjectId, default));
    }

    [Fact]
    public async Task RemoveContent_IsIdempotentAndUnbanRestoresNothing_D180()
    {
        RemovalSeed seed = await SeedAsync();

        Assert.True((await RemoveContentAsync(seed.SubjectId)).Succeeded);
        Assert.True((await RemoveContentAsync(seed.SubjectId)).Succeeded);

        await using (AsyncServiceScope unbanScope = _factory.Services.CreateAsyncScope())
        {
            AdminUserMutationResult unban = await unbanScope.ServiceProvider
                .GetRequiredService<UnbanUserUseCase>()
                .ExecuteAsync(seed.SubjectId, default);
            Assert.True(unban.Succeeded, unban.Code);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Null((await verify.DomainUsers.AsNoTracking()
            .SingleAsync(u => u.Id == seed.SubjectId)).BannedAt);
        Assert.Equal(
            SessionStatus.Canceled,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == seed.OwnedFutureId)).Status);
        Assert.Equal(
            PollStatus.Expired,
            (await verify.Polls.AsNoTracking().SingleAsync(p => p.Id == seed.OwnedPollId)).Status);
        Assert.True((await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == seed.ParticipatedFutureId && p.UserId == seed.SubjectId))
            .IsRemoved);
        Assert.Equal(
            JoinRequestStatus.Lapsed,
            (await verify.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == seed.JoinRequestId)).Status);
    }

    /// <summary>
    /// F1: the §6.2 atomicity for real, against Postgres. <see cref="AlwaysChangingDeletionScopeProvider"/>
    /// (<c>Sessions/SessionDeletionEffectsTests.cs</c>) forces every one of the runner's 3 attempts to
    /// disagree with reload, so <see cref="LockedSubjectScopeRunner"/> throws
    /// <see cref="DeletionScopeChangedException"/> on all three and the outer transaction rolls back
    /// each time — unlike the in-memory application-seam proof, a real rollback here actually undoes
    /// the ban row write, so this is the one place that can assert it landed nowhere.
    /// </summary>
    [Fact]
    public async Task RemoveContent_RollsBackTheBanWhenTheScopeNeverStabilizes_D180()
    {
        string suffix = Guid.NewGuid().ToString("N");
        UserId subjectId = await _factory.SeedUserAsync(email: $"removal-contended-{suffix}@example.test");

        SessionId ownedFutureId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Session ownedFuture = Session.Create(subjectId, Details("Contended owned", 180), [], _factory.Clock);
            _ = ownedFuture.DequeueEvents();
            ownedFutureId = ownedFuture.Id;
            context.Sessions.Add(ownedFuture);
            await context.SaveChangesAsync();
        }

        AdminUserMutationResult result;
        var counter = new DiscoveryCallCounter();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var alwaysChanging = new AlwaysChangingDeletionScopeProvider(
                scope.ServiceProvider.GetRequiredService<IDeletionScopeProvider>(),
                scope.ServiceProvider,
                _factory.Clock,
                counter);

            var useCase = new BanAndRemoveContentUseCase(
                scope.ServiceProvider.GetRequiredService<IUserRepository>(),
                alwaysChanging,
                scope.ServiceProvider.GetRequiredService<IDeletionResultApplier>(),
                scope.ServiceProvider.GetRequiredService<IAccountSessionService>(),
                scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                _factory.Clock,
                scope.ServiceProvider.GetRequiredService<ISessionNotificationMaterializer>(),
                scope.ServiceProvider.GetRequiredService<IPollNotificationMaterializer>(),
                scope.ServiceProvider.GetRequiredService<ISessionLockRepository>());

            result = await useCase.ExecuteAsync(subjectId, default);
        }

        Assert.False(result.Succeeded);
        Assert.Equal("scope_contended", result.Code);
        Assert.Equal(3, counter.Calls);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await verify.DomainUsers.AsNoTracking().SingleAsync(u => u.Id == subjectId)).BannedAt);
        Assert.NotEqual(
            SessionStatus.Canceled,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == ownedFutureId)).Status);
    }

    /// <summary>
    /// F2: the removal path shares its notification wiring with the deletion path — the counterpart
    /// on the canceled session gets the ordinary <c>SessionCanceled</c> row and the poll's registered
    /// voter gets the D-170 <c>PollExpired</c> row, and the banned subject, who caused both, gets
    /// neither.
    /// </summary>
    [Fact]
    public async Task RemoveContent_NotifiesCounterpartsButNeverTheSubject_D180()
    {
        string suffix = Guid.NewGuid().ToString("N");
        UserId subjectId = await _factory.SeedUserAsync(email: $"removal-notify-subject-{suffix}@example.test");
        UserId counterpartId = await _factory.SeedUserAsync(email: $"removal-notify-counterpart-{suffix}@example.test");
        UserId voterId = await _factory.SeedUserAsync(email: $"removal-notify-voter-{suffix}@example.test");

        SessionId sessionId;
        PollId pollId;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, subjectId, counterpartId, _factory.Clock);
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, subjectId, voterId, _factory.Clock);

            Session session = Session.Create(subjectId, Details("Notify owned", 180), [], _factory.Clock);
            session.Invite(counterpartId, _factory.Clock);
            session.Rsvp(ParticipantRef.ForUser(counterpartId), ParticipationStatus.Going, _factory.Clock);
            _ = session.DequeueEvents();
            sessionId = session.Id;
            context.Sessions.Add(session);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
                new CreatePollCommand(
                    subjectId,
                    "Notify owned poll",
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    Options: [new CreatePollOptionInput(now + Duration.FromDays(6), 60), new CreatePollOptionInput(now + Duration.FromDays(7), 60)],
                    DeadlineChoice: PollDeadlineChoice.Hours48,
                    InviteeUserIds: [voterId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            pollId = created.Poll!.Id;
        }

        AdminUserMutationResult result = await RemoveContentAsync(subjectId);
        Assert.True(result.Succeeded, result.Code);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(1, await verify.Notifications.CountAsync(n =>
            n.SessionId == sessionId &&
            n.Type == NotificationType.SessionCanceled &&
            n.RecipientUserId == counterpartId));
        Assert.Equal(1, await verify.Notifications.CountAsync(n =>
            n.PollId == pollId &&
            n.Type == NotificationType.PollExpired &&
            n.RecipientUserId == voterId));
        Assert.Equal(0, await verify.Notifications.CountAsync(n => n.RecipientUserId == subjectId));
    }

    private static Task<Instant?> LastSeenAsync(AppDbContext context, UserId subjectId) =>
        context.DomainUsers.AsNoTracking()
            .Where(user => user.Id == subjectId)
            .Select(user => EF.Property<Instant?>(user, UserActivityWriter.LastSeenProperty))
            .SingleAsync();

    private async Task<AdminUserMutationResult> RemoveContentAsync(UserId subjectId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BanAndRemoveContentUseCase>()
            .ExecuteAsync(subjectId, default);
    }

    private async Task<RemovalSeed> SeedAsync()
    {
        string suffix = Guid.NewGuid().ToString("N");
        UserId subjectId = await _factory.SeedUserAsync(email: $"removal-subject-{suffix}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"removal-friend-{suffix}@example.test");
        UserId hostId = await _factory.SeedUserAsync(email: $"removal-host-{suffix}@example.test");
        UserId requestHostId = await _factory.SeedUserAsync(email: $"removal-reqhost-{suffix}@example.test");

        Instant seenAt = _factory.Clock.GetCurrentInstant();
        SessionId ownedFutureId;
        SessionId ownedInProgressId;
        SessionId ownedCompletedId;
        SessionId participatedFutureId;
        JoinRequestId joinRequestId;

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, subjectId, friendId, _factory.Clock);

            // Completed history (§11's own exclusion) — confirmed and run to completion before any of
            // the removal-scoped sessions below are even created, so it predates the ban entirely.
            Session ownedCompleted = Session.Create(
                subjectId, Details("Owned completed", 15, durationMinutes: 15), [], _factory.Clock);
            ownedCompleted.Confirm(_factory.Clock);
            _factory.Clock.Advance(Duration.FromMinutes(31));
            ownedCompleted.Complete(_factory.Clock);
            _ = ownedCompleted.DequeueEvents();
            ownedCompletedId = ownedCompleted.Id;

            Session ownedFuture = Session.Create(subjectId, Details("Owned future", 180), [], _factory.Clock);
            _ = ownedFuture.DequeueEvents();
            ownedFutureId = ownedFuture.Id;

            Session ownedInProgress = Session.Create(
                subjectId, Details("Owned in-progress", 30, durationMinutes: 120), [], _factory.Clock);
            _ = ownedInProgress.DequeueEvents();
            ownedInProgressId = ownedInProgress.Id;

            Session participatedFuture = Session.Create(
                hostId, Details("Other's future", 180), [], _factory.Clock);
            participatedFuture.Invite(subjectId, _factory.Clock);
            participatedFuture.Rsvp(
                ParticipantRef.ForUser(subjectId), ParticipationStatus.Going, _factory.Clock);
            _ = participatedFuture.DequeueEvents();
            participatedFutureId = participatedFuture.Id;

            Session requestTarget = Session.Create(
                requestHostId, Details("Request target", 180), [], _factory.Clock);
            joinRequestId = requestTarget.OpenJoinRequest(subjectId, _factory.Clock).Id;
            _ = requestTarget.DequeueEvents();

            context.Sessions.AddRange(
                ownedCompleted, ownedFuture, ownedInProgress, participatedFuture, requestTarget);
            context.SessionReminderHandoffs.Add(new SessionReminderHandoff
            {
                Id = Guid.CreateVersion7(),
                SessionId = participatedFutureId,
                RecipientUserId = subjectId,
                DueAt = _factory.Clock.GetCurrentInstant() + Duration.FromHours(2),
                State = SessionReminderHandoffState.Pending,
                PlannedAt = _factory.Clock.GetCurrentInstant(),
                DeliveredAt = null,
                Version = 0,
                Attempts = 0,
                LastAttemptAt = null,
            });
            await context.SaveChangesAsync();

            // A banned account stays live, so the activity column must survive the removal.
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE users SET last_seen_at = {seenAt}
                WHERE id = {subjectId.Value}
                """);
        }

        PollId ownedPollId;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
                new CreatePollCommand(
                    subjectId,
                    "Owned poll",
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    Options: [new CreatePollOptionInput(now + Duration.FromDays(6), 60), new CreatePollOptionInput(now + Duration.FromDays(7), 60)],
                    DeadlineChoice: PollDeadlineChoice.Hours48,
                    InviteeUserIds: [friendId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            ownedPollId = created.Poll!.Id;
        }

        // "Future" and "in progress" are both evaluated at the removal instant: the +30-minute
        // session has started (and its 120-minute run has not ended) while the +180-minute ones have
        // not, without any session ever being created in the past.
        _factory.Clock.Advance(Duration.FromMinutes(60));

        return new RemovalSeed(
            subjectId,
            ownedFutureId,
            ownedInProgressId,
            ownedCompletedId,
            participatedFutureId,
            joinRequestId,
            ownedPollId,
            seenAt);
    }

    private SessionDetails Details(string title, int leadMinutes, int durationMinutes = 60) => new(
        SessionTitle.Create(title),
        _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(leadMinutes),
        Duration15.FromMinutes(durationMinutes),
        Platform.NotApplicable,
        null,
        SessionNotes.Empty,
        minAttendance: 1,
        maxCapacity: null,
        visibility: ContentVisibility.Private);

    private sealed record RemovalSeed(
        UserId SubjectId,
        SessionId OwnedFutureId,
        SessionId OwnedInProgressId,
        SessionId OwnedCompletedId,
        SessionId ParticipatedFutureId,
        JoinRequestId JoinRequestId,
        PollId OwnedPollId,
        Instant SeenAt);
}
