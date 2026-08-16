using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Admin;
using PartyUp.Application.Deletion;
using PartyUp.Application.Tests.Deletion;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Tests.Admin;

/// <summary>
/// D-180's optional content removal (PHASE8_DESIGN.md §3 S2) at the application seam: the ban and the
/// §11 steps 1–3 effects share one transaction and one A-068-ordered lock envelope, and every effect
/// deletion adds beyond those three steps is asserted <em>absent</em>.
/// </summary>
public sealed class BanAndRemoveContentUseCaseTests
{
    private static readonly string[] ExpectedLockOrder = ["lock-users", "find-for-update"];

    [Fact]
    public async Task Execute_BansAndRemovesContentInOneTransaction_D180()
    {
        RemovalWorld world = RemovalWorld.Build();

        AdminUserMutationResult result = await world.UseCase.ExecuteAsync(world.Subject.Id, default);

        Assert.True(result.Succeeded);
        Assert.True(world.Subject.IsBanned);
        Assert.Equal(1, world.Sessions.Rotations);

        // One outermost transaction for both halves — never a nested BanUserUseCase.ExecuteAsync.
        Assert.Equal(1, world.UnitOfWork.Executions);

        ContentRemovalResult removal = Assert.Single(world.Applier.ContentRemovals);
        Assert.Equal(world.OwnedFuture.Id, Assert.Single(removal.CanceledSessions));
        Assert.Equal(SessionStatus.Canceled, world.OwnedFuture.Status);
        Assert.Equal(world.OthersFuture.Id, Assert.Single(removal.RemovedFromSessions));
        Assert.Equal(world.OwnJoinRequestId, Assert.Single(removal.LapsedJoinRequests));
        Assert.Contains(
            removal.CanceledReminders,
            reminder => reminder.SessionId == world.OthersFuture.Id && reminder.UserId == world.Subject.Id);
    }

    /// <summary>
    /// The deletion applier overload is what carries tombstone-only work (friendship and block
    /// removal, the purge scopes, the notification purge). The ban path must never reach it — and it
    /// writes no journal entry, releases no username and leaves the lifecycle alone.
    /// </summary>
    [Fact]
    public async Task Execute_RunsNoDeletionEffects_D180()
    {
        RemovalWorld world = RemovalWorld.Build();

        await world.UseCase.ExecuteAsync(world.Subject.Id, default);

        Assert.Empty(world.Applier.Applications);
        Assert.False(world.Subject.IsTombstoned);
        Assert.False(world.Subject.IsPendingDeletion);
        Assert.True(world.Subject.IsActive);
        Assert.NotNull(world.Subject.Username);
        Assert.NotNull(world.Subject.DisplayName);
    }

    /// <summary>
    /// D-180: an unban restores access, never content. The canceled session and removed participation
    /// survive the unban untouched.
    /// </summary>
    [Fact]
    public async Task Unban_DoesNotRestoreRemovedContent_D180()
    {
        RemovalWorld world = RemovalWorld.Build();
        await world.UseCase.ExecuteAsync(world.Subject.Id, default);

        var users = new FakeUserRepository();
        users.Users[world.Subject.Id] = world.Subject;
        AdminUserMutationResult unban = await new UnbanUserUseCase(users, new FakeUnitOfWork())
            .ExecuteAsync(world.Subject.Id, default);

        Assert.True(unban.Succeeded);
        Assert.False(world.Subject.IsBanned);
        Assert.Equal(SessionStatus.Canceled, world.OwnedFuture.Status);
        Assert.True(world.OthersFuture.Find(ParticipantRef.ForUser(world.Subject.Id))!.IsRemoved);
    }

    [Fact]
    public async Task Execute_IsIdempotent_D180()
    {
        RemovalWorld world = RemovalWorld.Build();

        Assert.True((await world.UseCase.ExecuteAsync(world.Subject.Id, default)).Succeeded);
        Assert.True((await world.UseCase.ExecuteAsync(world.Subject.Id, default)).Succeeded);

        ContentRemovalResult second = world.Applier.ContentRemovals[1];
        Assert.Empty(second.CanceledSessions);
        Assert.Empty(second.RemovedFromSessions);
        Assert.Empty(second.LapsedJoinRequests);
        Assert.Empty(second.CanceledReminders);
        Assert.Equal(SessionStatus.Canceled, world.OwnedFuture.Status);
    }

    /// <summary>
    /// A-068 / §6.2: the union lock is taken first, and the ban is applied to the entity the envelope's
    /// own <c>FindForUpdateAsync</c> returned — never a subject-first lock ahead of the union, which is
    /// exactly the inversion the shipped envelope forbids (and what a nested <c>BanUserUseCase</c>
    /// would do).
    /// </summary>
    [Fact]
    public async Task Execute_LocksTheWholeUnionBeforeReadingTheSubject_A068_D180()
    {
        RemovalWorld world = RemovalWorld.Build();

        await world.UseCase.ExecuteAsync(world.Subject.Id, default);

        Assert.Equal(ExpectedLockOrder, world.Trace);
        IReadOnlyCollection<UserId> locked = Assert.Single(world.Locks.LockedUserBatches);
        Assert.Contains(world.Subject.Id, locked);
        Assert.Contains(world.OtherOrganizerId, locked);
        Assert.False(world.BannedBeforeTheLock);
    }

    [Fact]
    public async Task Execute_ReportsNotFoundForAnUnknownSubject_D180()
    {
        RemovalWorld world = RemovalWorld.Build();

        AdminUserMutationResult result = await world.UseCase.ExecuteAsync(UserId.New(), default);

        Assert.False(result.Succeeded);
        Assert.Equal("not_found", result.Code);
        Assert.Empty(world.Applier.ContentRemovals);
    }

    /// <summary>
    /// The §6.2 bounded retry, mirroring <c>_D092</c>: a candidate set that changes once between
    /// discovery and reload retries in a fresh transaction and succeeds; one that never stabilizes
    /// exhausts at three attempts, writes nothing, and reports contention rather than a silent
    /// success — so no ban is committed either (the ban lives inside the same rolled-back transaction).
    /// </summary>
    [Fact]
    public async Task Execute_RetriesABoundedNumberOfTimesWhenTheScopeChanges_D180_D092()
    {
        User recovering = ActiveUser(out MutableClock recoveringClock, "ABCDEFGHJK");
        var recoveringScope = new RecoveringDeletionScopeProvider(
            recovering.Id,
            UserId.New(),
            mismatchedAttempts: 1);
        var recoveringApplier = new FakeDeletionResultApplier(new FakeAccountGateway());
        AdminUserMutationResult retried = await CreateUseCase(
            recovering, recoveringScope, recoveringApplier, recoveringClock).ExecuteAsync(recovering.Id, default);

        Assert.True(retried.Succeeded);
        Assert.Equal(2, recoveringScope.DiscoverCalls);
        Assert.Single(recoveringApplier.ContentRemovals);

        User stuck = ActiveUser(out MutableClock stuckClock, "KLMNPQRSTU");
        var neverStable = new RecoveringDeletionScopeProvider(
            stuck.Id,
            UserId.New(),
            mismatchedAttempts: int.MaxValue);
        var stuckApplier = new FakeDeletionResultApplier(new FakeAccountGateway());
        AdminUserMutationResult exhausted = await CreateUseCase(
            stuck, neverStable, stuckApplier, stuckClock).ExecuteAsync(stuck.Id, default);

        Assert.False(exhausted.Succeeded);
        Assert.Equal("scope_contended", exhausted.Code);
        Assert.Equal(3, neverStable.DiscoverCalls);
        Assert.Empty(stuckApplier.ContentRemovals);
    }

    /// <summary>
    /// Atomicity: the ban is applied <em>inside</em> the single transaction the removal runs in, and a
    /// failing removal aborts that transaction — so no ban is left behind. The in-memory fake cannot
    /// undo an aggregate mutation, so what is asserted here is the property that makes the real
    /// rollback work: the subject is unbanned when the transaction opens, the failure escapes
    /// <c>ExecuteAsync</c>, and there is exactly one transaction to roll back.
    /// </summary>
    [Fact]
    public async Task Execute_LeavesNoCommittedBanWhenTheRemovalFails_D180()
    {
        RemovalWorld world = RemovalWorld.Build(applier: new ThrowingDeletionResultApplier());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.UseCase.ExecuteAsync(world.Subject.Id, default));

        Assert.False(world.BannedBeforeTheLock);
        Assert.Equal(1, world.UnitOfWork.Executions);
    }

    private static BanAndRemoveContentUseCase CreateUseCase(
        User subject,
        IDeletionScopeProvider scopeProvider,
        IDeletionResultApplier applier,
        IClock clock)
    {
        var users = new FakeUserRepository();
        users.Users[subject.Id] = subject;
        return new BanAndRemoveContentUseCase(
            users,
            scopeProvider,
            applier,
            new FakeAccountSessionService(),
            new FakeUnitOfWork(),
            clock,
            new NoOpSessionNotificationMaterializer(),
            new NoOpPollNotificationMaterializer());
    }

    private static User ActiveUser(out MutableClock clock, string friendCode)
    {
        clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create(friendCode),
            AgeGateSource.SelfAttested,
            clock);
        user.CompleteOnboarding(
            Username.Create("subject"),
            DisplayName.Create("Subject"),
            Avatar.FromEmoji("🙂"),
            DateTimeZone.Utc,
            Locale.Create("en"),
            clock);
        return user;
    }

    /// <summary>
    /// One subject who organizes a not-started session, participates in someone else's not-started
    /// session, and holds an open join request on a third — the three surfaces steps 1–3 touch.
    /// </summary>
    private sealed record RemovalWorld(
        BanAndRemoveContentUseCase UseCase,
        User Subject,
        UserId OtherOrganizerId,
        Session OwnedFuture,
        Session OthersFuture,
        JoinRequestId OwnJoinRequestId,
        FakeDeletionResultApplier Applier,
        FakeAccountSessionService Sessions,
        FakeUnitOfWork UnitOfWork,
        RecordingSessionLockRepository Locks,
        List<string> Trace,
        Func<bool> BannedAtLock)
    {
        internal bool BannedBeforeTheLock => BannedAtLock();

        internal static RemovalWorld Build(IDeletionResultApplier? applier = null)
        {
            var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
            User subject = ActiveUser(out MutableClock _, "UVWXYZ2345");
            UserId otherOrganizerId = UserId.New();
            UserId requestHostId = UserId.New();

            Session ownedFuture = Session.Create(subject.Id, Details("Owned", clock), [], clock);
            Session othersFuture = Session.Create(otherOrganizerId, Details("Others", clock), [], clock);
            othersFuture.Invite(subject.Id, clock);
            othersFuture.Rsvp(ParticipantRef.ForUser(subject.Id), ParticipationStatus.Going, clock);
            Session requestTarget = Session.Create(requestHostId, Details("Request", clock), [], clock);
            JoinRequest ownRequest = requestTarget.OpenJoinRequest(subject.Id, clock);

            foreach (Session session in new[] { ownedFuture, othersFuture, requestTarget })
            {
                _ = session.DequeueEvents();
            }

            var scope = new DeletionScope(
                [ownedFuture, othersFuture, requestTarget],
                [],
                [],
                [],
                null,
                [],
                []);
            List<string> trace = [];
            var scopeProvider = new TracingScopeProvider(subject.Id, scope);
            var locks = new RecordingSessionLockRepository();
            var users = new TracingUserRepository(subject, trace);
            var recordingLocks = new TracingSessionLockRepository(locks, trace, subject);
            var resultApplier = applier ?? new FakeDeletionResultApplier(new FakeAccountGateway());
            var sessions = new FakeAccountSessionService();
            var unitOfWork = new FakeUnitOfWork();
            var useCase = new BanAndRemoveContentUseCase(
                users,
                scopeProvider,
                resultApplier,
                sessions,
                unitOfWork,
                clock,
                new NoOpSessionNotificationMaterializer(),
                new NoOpPollNotificationMaterializer(),
                recordingLocks);

            return new RemovalWorld(
                useCase,
                subject,
                otherOrganizerId,
                ownedFuture,
                othersFuture,
                ownRequest.Id,
                resultApplier as FakeDeletionResultApplier
                    ?? new FakeDeletionResultApplier(new FakeAccountGateway()),
                sessions,
                unitOfWork,
                locks,
                trace,
                () => recordingLocks.SubjectWasBannedAtLockTime);
        }

        private static SessionDetails Details(string title, MutableClock clock) => new(
            SessionTitle.Create(title),
            clock.GetCurrentInstant() + Duration.FromDays(3),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Private);
    }

    private sealed class TracingScopeProvider(UserId subject, DeletionScope scope) : IDeletionScopeProvider
    {
        public Task<DeletionScope> LoadAsync(UserId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This test exercises the widened protocol only.");

        public Task<IReadOnlyCollection<UserId>> DiscoverAffectedUserIdsAsync(
            UserId id,
            CancellationToken cancellationToken) =>
            Task.FromResult(IDeletionScopeProvider.ExtractAffectedUserIds(subject, scope));

        public Task<DeletionScope> LoadForUpdateAsync(UserId id, CancellationToken cancellationToken) =>
            Task.FromResult(scope);
    }

    private sealed class TracingUserRepository(User user, List<string> trace) : IUserRepository
    {
        public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken)
        {
            trace.Add("find");
            return Task.FromResult<User?>(id == user.Id ? user : null);
        }

        public Task<User?> FindForUpdateAsync(UserId id, CancellationToken cancellationToken)
        {
            trace.Add("find-for-update");
            return Task.FromResult<User?>(id == user.Id ? user : null);
        }

        public Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
            Instant now,
            int max,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UserId>>([]);

        public void Add(User added) => throw new NotSupportedException();
    }

    private sealed class TracingSessionLockRepository(
        RecordingSessionLockRepository inner,
        List<string> trace,
        User subject) : ISessionLockRepository
    {
        internal bool SubjectWasBannedAtLockTime { get; private set; }

        public Task LockUsersAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken)
        {
            trace.Add("lock-users");
            SubjectWasBannedAtLockTime |= subject.IsBanned;
            return inner.LockUsersAsync(userIds, cancellationToken);
        }

        public Task LockSessionsAsync(
            IReadOnlyCollection<SessionId> sessionIds,
            CancellationToken cancellationToken) =>
            inner.LockSessionsAsync(sessionIds, cancellationToken);
    }

    private sealed class ThrowingDeletionResultApplier : IDeletionResultApplier
    {
        public Task ApplyAsync(
            DeletionExecutionResult result,
            UserId subject,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ApplyContentRemovalAsync(
            ContentRemovalResult result,
            UserId subject,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The reminder-cancel tail failed.");
    }

    private sealed class NoOpSessionNotificationMaterializer : ISessionNotificationMaterializer
    {
        public Task MaterializeAsync(
            Session session,
            IReadOnlyList<IDomainEvent> events,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoOpPollNotificationMaterializer : IPollNotificationMaterializer
    {
        public Task MaterializeAsync(
            PartyUp.Domain.Polls.Poll poll,
            IReadOnlyList<IDomainEvent> events,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
