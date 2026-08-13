using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Deletion;
using PartyUp.Application.Email;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Tests.Deletion;

public sealed class RequestAccountDeletionUseCaseTests
{
    [Fact]
    public async Task Execute_StartsASevenDayGrace_D148()
    {
        TestContext context = Create(active: true);

        AccountDeletionRequestResult result = await context.UseCase.ExecuteAsync(
            new RequestAccountDeletionCommand(context.User.Id, true),
            default);

        Assert.True(result.Succeeded);
        Assert.Equal(context.Clock.GetCurrentInstant() + Duration.FromDays(7), result.GraceEndsAt);
        Assert.True(context.User.IsPendingDeletion);
    }

    [Fact]
    public async Task Execute_SignsOutEveryDevice_D165()
    {
        TestContext context = Create(active: true);

        await context.UseCase.ExecuteAsync(
            new RequestAccountDeletionCommand(context.User.Id, true),
            default);

        Assert.Equal(1, context.Sessions.Rotations);
    }

    [Fact]
    public async Task Execute_MailsTheCancellationDisclosure_D158()
    {
        TestContext context = Create(active: true);

        await context.UseCase.ExecuteAsync(
            new RequestAccountDeletionCommand(context.User.Id, true),
            default);

        EmailMessage message = Assert.Single(context.Email.Messages);
        Assert.Equal(EmailTemplate.DeletionRequested, message.Template);
        Assert.True(message.Model.ContainsKey("GraceEndsAt"));
        Assert.False(message.Model.ContainsKey("Token"));
        Assert.Equal("AsTyped@Example.test", message.ToAddress);
    }

    [Theory]
    [InlineData(
        "en",
        "America/New_York",
        "August 7, 2026 at 8:00 AM America/New_York")]
    [InlineData(
        "pt-BR",
        "America/Fortaleza",
        "7 de agosto de 2026 \u00e0s 09:00 America/Fortaleza")]
    [InlineData(
        "es",
        "Europe/Madrid",
        "7 de agosto de 2026 a las 14:00 Europe/Madrid")]
    public async Task Execute_FormatsGraceEndInTheRecipientsZoneAndLocale_D060(
        string locale,
        string timeZoneId,
        string expected)
    {
        TestContext context = Create(
            active: true,
            locale,
            DateTimeZoneProviders.Tzdb[timeZoneId]);

        await context.UseCase.ExecuteAsync(
            new RequestAccountDeletionCommand(context.User.Id, true),
            default);

        EmailMessage message = Assert.Single(context.Email.Messages);
        Assert.Equal(locale, message.Locale.Value);
        Assert.Equal(expected, message.Model["GraceEndsAt"]);
    }

    [Fact]
    public async Task Execute_RequiresReauthentication_D165()
    {
        TestContext context = Create(active: true);

        AccountDeletionRequestResult result = await context.UseCase.ExecuteAsync(
            new RequestAccountDeletionCommand(context.User.Id, false),
            default);

        Assert.Equal("reauth_required", result.Code);
        Assert.True(context.User.IsActive);
        Assert.Equal(0, context.UnitOfWork.Executions);
    }

    [Fact]
    public async Task Execute_DeletesAnAccountAbandonedDuringOnboarding_D072()
    {
        TestContext context = Create(active: false);

        AccountDeletionRequestResult result = await context.UseCase.ExecuteAsync(
            new RequestAccountDeletionCommand(context.User.Id, true),
            default);

        Assert.True(result.Succeeded);
        Assert.True(context.User.IsPendingDeletion);
        Assert.False(context.User.IsProfileComplete);
    }

    private static TestContext Create(
        bool active,
        string locale = "en",
        DateTimeZone? timeZone = null)
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            clock);
        if (active)
        {
            Complete(
                user,
                clock,
                timeZone ?? DateTimeZoneProviders.Tzdb["America/Fortaleza"],
                Locale.Create(locale));
        }

        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var accounts = new FakeAccountGateway();
        accounts.Emails[user.Id] = new AccountEmailInfo("AsTyped@Example.test", true);
        var sessions = new FakeAccountSessionService();
        var unitOfWork = new FakeUnitOfWork();
        var email = new FakeEmailDispatcher();
        var useCase = new RequestAccountDeletionUseCase(
            users,
            accounts,
            sessions,
            unitOfWork,
            email,
            clock);
        return new TestContext(useCase, user, sessions, unitOfWork, email, clock);
    }

    private sealed record TestContext(
        RequestAccountDeletionUseCase UseCase,
        User User,
        FakeAccountSessionService Sessions,
        FakeUnitOfWork UnitOfWork,
        FakeEmailDispatcher Email,
        MutableClock Clock);

    internal static void Complete(User user, IClock clock) =>
        Complete(
            user,
            clock,
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"));

    private static void Complete(
        User user,
        IClock clock,
        DateTimeZone timeZone,
        Locale locale) =>
        user.CompleteOnboarding(
            Username.Create("player"),
            DisplayName.Create("Player"),
            Avatar.FromEmoji("🎮"),
            timeZone,
            locale,
            clock);
}

public sealed class ConfirmPublicDeletionUseCaseTests
{
    [Fact]
    public async Task Execute_VerifiesIdentityBeforeStartingTheGrace_D072()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("KLMNPQRSTU"),
            AgeGateSource.SelfAttested,
            clock);
        RequestAccountDeletionUseCaseTests.Complete(user, clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var accounts = new FakeAccountGateway();
        var tokens = new FakeTokenService();
        var request = new RequestAccountDeletionUseCase(
            users,
            accounts,
            new FakeAccountSessionService(),
            new FakeUnitOfWork(),
            new FakeEmailDispatcher(),
            clock);
        var useCase = new ConfirmPublicDeletionUseCase(tokens, request, new FakeUnitOfWork());

        AccountDeletionRequestResult rejected = await useCase.ExecuteAsync("unknown", default);
        Assert.Equal("invalid_token", rejected.Code);
        Assert.True(user.IsActive);

        tokens.RedemptionResults.Enqueue(new TokenRedemption(
            true,
            null,
            AuthTokenPurpose.DeletionRequestConfirmation,
            user.Id,
            null,
            null,
            null,
            clock.GetCurrentInstant()));
        AccountDeletionRequestResult accepted = await useCase.ExecuteAsync("proven", default);

        Assert.True(accepted.Succeeded);
        Assert.True(user.IsPendingDeletion);
    }
}

public sealed class ExecuteDeletionForUserUseCaseTests
{
    [Fact]
    public async Task Execute_TombstonesOnlyAnElapsedGrace_D148()
    {
        ExecutionContext context = Create();

        Assert.False(await context.UseCase.ExecuteAsync(context.User.Id, default));
        Assert.True(context.User.IsPendingDeletion);
        Assert.Empty(context.ResultApplier.Applications);

        context.Clock.Advance(User.DeletionGracePeriod);
        Assert.True(await context.UseCase.ExecuteAsync(context.User.Id, default));
        Assert.True(context.User.IsTombstoned);
        Assert.Contains(context.User.Id, context.Accounts.PurgedCredentials);
        Assert.Single(context.ResultApplier.Applications);
    }

    [Fact]
    public async Task Execute_AppendsTheJournalEntry_D092()
    {
        ExecutionContext context = Create();
        context.Clock.Advance(User.DeletionGracePeriod);

        await context.UseCase.ExecuteAsync(context.User.Id, default);

        DeletionJournalEntry entry = Assert.Single(context.Journal.Entries);
        Assert.Equal(context.User.Id, entry.UserId);
        Assert.True(context.Username.IsReserved);
        Assert.Equal(1, context.Shipper.Calls);
    }

    [Fact]
    public async Task Execute_IsIdempotent_D148()
    {
        ExecutionContext context = Create();
        context.Clock.Advance(User.DeletionGracePeriod);

        Assert.True(await context.UseCase.ExecuteAsync(context.User.Id, default));
        Assert.False(await context.UseCase.ExecuteAsync(context.User.Id, default));

        Assert.Single(context.Journal.Entries);
        Assert.Single(context.Accounts.PurgedCredentials);
        Assert.Single(context.ResultApplier.Applications);
    }

    [Fact]
    public async Task Execute_AppliesTheDeletionResultBeforeCredentialPurge_D092()
    {
        ExecutionContext context = Create();
        context.Clock.Advance(User.DeletionGracePeriod);

        await context.UseCase.ExecuteAsync(context.User.Id, default);

        CapturedDeletionApplication application = Assert.Single(
            context.ResultApplier.Applications);
        Assert.Equal(context.User.Id, application.Subject);
        Assert.Equal(context.User.Id, application.Result.Journal.UserId);
        Assert.Same(context.Username, application.Result.ReservedUsername);
        Assert.True(application.BeforeCredentialPurge);
    }

    [Fact]
    public async Task Execute_LocksDiscoveredCounterpartsAndMaterializesSessionEvents_WhenTheCandidateSetIsUnchanged()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            clock);
        RequestAccountDeletionUseCaseTests.Complete(user, clock);
        user.RequestDeletion(clock);
        clock.Advance(User.DeletionGracePeriod);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var friendId = UserId.New();
        var scope = new StableDeletionScopeProvider(user.Id, friendId);
        var sessionLocks = new RecordingSessionLockRepository();
        var resultApplier = new FakeDeletionResultApplier(new FakeAccountGateway());
        var useCase = new ExecuteDeletionForUserUseCase(
            users,
            scope,
            resultApplier,
            new FakeAccountGateway(),
            new FakeDeletionJournal(),
            new FakeDeletionJournalShipper(),
            new FakeUnitOfWork(),
            clock,
            sessionLocks,
            new RecordingSessionNotificationMaterializer());

        Assert.True(await useCase.ExecuteAsync(user.Id, default));

        Assert.Equal(1, scope.DiscoverCalls);
        Assert.Equal(1, scope.ReloadCalls);
        IReadOnlyCollection<UserId> lockedBatch = Assert.Single(sessionLocks.LockedUserBatches);
        Assert.Equal(
            new HashSet<UserId> { user.Id, friendId },
            lockedBatch.ToHashSet());
        Assert.Single(resultApplier.Applications);
    }

    [Fact]
    public async Task Execute_RetriesOnceWhenTheCandidateSetChangedBetweenDiscoveryAndReload_D092()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("KLMNPQRSTU"),
            AgeGateSource.SelfAttested,
            clock);
        RequestAccountDeletionUseCaseTests.Complete(user, clock);
        user.RequestDeletion(clock);
        clock.Advance(User.DeletionGracePeriod);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var friendId = UserId.New();
        var scope = new RecoveringDeletionScopeProvider(user.Id, friendId, mismatchedAttempts: 1);
        var resultApplier = new FakeDeletionResultApplier(new FakeAccountGateway());
        var useCase = new ExecuteDeletionForUserUseCase(
            users,
            scope,
            resultApplier,
            new FakeAccountGateway(),
            new FakeDeletionJournal(),
            new FakeDeletionJournalShipper(),
            new FakeUnitOfWork(),
            clock);

        Assert.True(await useCase.ExecuteAsync(user.Id, default));

        Assert.Equal(2, scope.DiscoverCalls);
        Assert.Single(resultApplier.Applications);
    }

    [Fact]
    public async Task Execute_ExhaustsTheBoundedRetryAndReturnsFalse_WhenTheCandidateSetNeverStabilizes_D092()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("STUVWXYZ23"),
            AgeGateSource.SelfAttested,
            clock);
        RequestAccountDeletionUseCaseTests.Complete(user, clock);
        user.RequestDeletion(clock);
        clock.Advance(User.DeletionGracePeriod);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var friendId = UserId.New();
        var scope = new RecoveringDeletionScopeProvider(user.Id, friendId, mismatchedAttempts: int.MaxValue);
        var resultApplier = new FakeDeletionResultApplier(new FakeAccountGateway());
        var useCase = new ExecuteDeletionForUserUseCase(
            users,
            scope,
            resultApplier,
            new FakeAccountGateway(),
            new FakeDeletionJournal(),
            new FakeDeletionJournalShipper(),
            new FakeUnitOfWork(),
            clock);

        Assert.False(await useCase.ExecuteAsync(user.Id, default));

        Assert.Equal(3, scope.DiscoverCalls);
        Assert.Empty(resultApplier.Applications);
    }

    private static ExecutionContext Create()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("UVWXYZ2345"),
            AgeGateSource.SelfAttested,
            clock);
        RequestAccountDeletionUseCaseTests.Complete(user, clock);
        UsernameRegistryEntry username = UsernameRegistryEntry.ClaimNew(
            user.Username!.Value,
            user.Id);
        user.RequestDeletion(clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var accounts = new FakeAccountGateway();
        var journal = new FakeDeletionJournal();
        var shipper = new FakeDeletionJournalShipper();
        var resultApplier = new FakeDeletionResultApplier(accounts);
        var useCase = new ExecuteDeletionForUserUseCase(
            users,
            new FakeDeletionScopeProvider(username),
            resultApplier,
            accounts,
            journal,
            shipper,
            new FakeUnitOfWork(),
            clock);
        return new ExecutionContext(
            useCase,
            user,
            username,
            accounts,
            resultApplier,
            journal,
            shipper,
            clock);
    }

    private sealed record ExecutionContext(
        ExecuteDeletionForUserUseCase UseCase,
        User User,
        UsernameRegistryEntry Username,
        FakeAccountGateway Accounts,
        FakeDeletionResultApplier ResultApplier,
        FakeDeletionJournal Journal,
        FakeDeletionJournalShipper Shipper,
        MutableClock Clock);
}

internal sealed record CapturedDeletionApplication(
    DeletionExecutionResult Result,
    UserId Subject,
    bool BeforeCredentialPurge);

internal sealed class FakeDeletionResultApplier(FakeAccountGateway accounts)
    : IDeletionResultApplier
{
    internal List<CapturedDeletionApplication> Applications { get; } = [];

    internal List<ContentRemovalResult> ContentRemovals { get; } = [];

    public Task ApplyAsync(
        DeletionExecutionResult result,
        UserId subject,
        CancellationToken cancellationToken)
    {
        Applications.Add(new CapturedDeletionApplication(
            result,
            subject,
            accounts.PurgedCredentials.Count == 0));
        return Task.CompletedTask;
    }

    public Task ApplyContentRemovalAsync(
        ContentRemovalResult result,
        UserId subject,
        CancellationToken cancellationToken)
    {
        ContentRemovals.Add(result);
        return Task.CompletedTask;
    }
}

internal sealed class MutableClock(Instant current) : IClock
{
    public Instant GetCurrentInstant() => current;

    public void Advance(Duration duration) => current += duration;
}

internal sealed class FakeDeletionScopeProvider(UsernameRegistryEntry username)
    : IDeletionScopeProvider
{
    public Task<DeletionScope> LoadAsync(UserId id, CancellationToken cancellationToken) =>
        Task.FromResult(new DeletionScope([], [], [], [], username));
}

/// <summary>Discover and reload always agree — the §6.2 happy path (attempt 1 always succeeds).</summary>
internal sealed class StableDeletionScopeProvider(UserId subject, UserId friendId)
    : IDeletionScopeProvider
{
    private readonly DeletionScope _scope = BuildScope(subject, friendId);

    internal int DiscoverCalls { get; private set; }

    internal int ReloadCalls { get; private set; }

    public Task<DeletionScope> LoadAsync(UserId id, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This test exercises the widened protocol only.");

    public Task<IReadOnlyCollection<UserId>> DiscoverAffectedUserIdsAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        DiscoverCalls++;
        return Task.FromResult(IDeletionScopeProvider.ExtractAffectedUserIds(subject, _scope));
    }

    public Task<DeletionScope> LoadForUpdateAsync(UserId id, CancellationToken cancellationToken)
    {
        ReloadCalls++;
        return Task.FromResult(_scope);
    }

    private static DeletionScope BuildScope(UserId subject, UserId friendId)
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User subjectUser = MinimalUser(subject, "ABCDEFGHJK", clock);
        User friendUser = MinimalUser(friendId, "KLMNPQRSTU", clock);
        Friendship friendship = Friendship.Request(subjectUser, friendUser, clock);
        _ = friendship.DequeueEvents();
        return new DeletionScope([], [], [friendship], [], null);
    }

    private static User MinimalUser(UserId id, string friendCode, IClock clock)
    {
        User user = User.BeginOnboarding(id, FriendCode.Create(friendCode), AgeGateSource.SelfAttested, clock);
        RequestAccountDeletionUseCaseTests.Complete(user, clock);
        return user;
    }
}

/// <summary>
/// Discover disagrees with the reload's recomputed set for <paramref name="mismatchedAttempts"/>
/// attempts, then agrees — proving the bounded outer retry of §6.2 and its eventual exhaustion.
/// </summary>
internal sealed class RecoveringDeletionScopeProvider(UserId subject, UserId friendId, int mismatchedAttempts)
    : IDeletionScopeProvider
{
    private readonly DeletionScope _scope = StableScopeFor(subject, friendId);

    internal int DiscoverCalls { get; private set; }

    public Task<DeletionScope> LoadAsync(UserId id, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This test exercises the widened protocol only.");

    public Task<IReadOnlyCollection<UserId>> DiscoverAffectedUserIdsAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        DiscoverCalls++;
        IReadOnlyCollection<UserId> discovered = DiscoverCalls <= mismatchedAttempts
            ? [subject]
            : IDeletionScopeProvider.ExtractAffectedUserIds(subject, _scope);
        return Task.FromResult(discovered);
    }

    public Task<DeletionScope> LoadForUpdateAsync(UserId id, CancellationToken cancellationToken) =>
        Task.FromResult(_scope);

    private static DeletionScope StableScopeFor(UserId subject, UserId friendId)
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User subjectUser = User.BeginOnboarding(
            subject,
            FriendCode.Create("STUVWXYZ23"),
            AgeGateSource.SelfAttested,
            clock);
        RequestAccountDeletionUseCaseTests.Complete(subjectUser, clock);
        User friendUser = User.BeginOnboarding(
            friendId,
            FriendCode.Create("YZ23456789"),
            AgeGateSource.SelfAttested,
            clock);
        RequestAccountDeletionUseCaseTests.Complete(friendUser, clock);
        Friendship friendship = Friendship.Request(subjectUser, friendUser, clock);
        _ = friendship.DequeueEvents();
        return new DeletionScope([], [], [friendship], [], null);
    }
}

internal sealed class RecordingSessionLockRepository : ISessionLockRepository
{
    internal List<IReadOnlyCollection<UserId>> LockedUserBatches { get; } = [];

    public Task LockUsersAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken)
    {
        LockedUserBatches.Add(userIds);
        return Task.CompletedTask;
    }

    public Task LockSessionsAsync(IReadOnlyCollection<SessionId> sessionIds, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class RecordingSessionNotificationMaterializer : ISessionNotificationMaterializer
{
    internal List<Session> Materialized { get; } = [];

    public Task MaterializeAsync(
        Session session,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken)
    {
        Materialized.Add(session);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDeletionJournal : IDeletionJournal
{
    public List<DeletionJournalEntry> Entries { get; } = [];

    public void Append(DeletionJournalEntry entry) => Entries.Add(entry);

    public Task<IReadOnlyList<DeletionJournalEntry>> ReadSinceAsync(
        Instant since,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeletionJournalEntry>>(
            Entries.Where(entry => entry.ExecutedAt >= since).Take(limit).ToArray());
}

internal sealed class FakeDeletionJournalShipper : IDeletionJournalShipper
{
    public int Calls { get; private set; }

    public Task<int> ShipPendingAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(1);
    }
}
