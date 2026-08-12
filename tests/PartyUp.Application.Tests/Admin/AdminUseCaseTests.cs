using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Admin;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Tests.Admin;

public sealed class BanUserUseCaseTests
{
    [Fact]
    public async Task Execute_SignsOutEveryDevice_D180()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 7, 31, 13, 0));
        User user = ActiveUser(clock);
        var users = new LockingUserRepository(user);
        var sessions = new FakeAccountSessionService();
        var unitOfWork = new FakeUnitOfWork();
        var useCase = new BanUserUseCase(users, sessions, unitOfWork, clock);

        AdminUserMutationResult result = await useCase.ExecuteAsync(user.Id, default);

        Assert.True(result.Succeeded);
        Assert.Equal(clock.GetCurrentInstant(), user.BannedAt);
        Assert.Equal(1, sessions.Rotations);
        Assert.Equal(1, unitOfWork.Executions);
        Assert.Equal(1, users.ForUpdateReads);
        Assert.Equal(0, users.UnlockedReads);
    }

    private static User ActiveUser(IClock clock)
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            clock);
        user.CompleteOnboarding(
            Username.Create("adminsubject"),
            DisplayName.Create("Admin Subject"),
            Avatar.FromEmoji("🙂"),
            DateTimeZone.Utc,
            Locale.Create("en"),
            clock);
        return user;
    }

    private sealed class LockingUserRepository(User user) : IUserRepository
    {
        internal int ForUpdateReads { get; private set; }

        internal int UnlockedReads { get; private set; }

        public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken)
        {
            UnlockedReads++;
            return Task.FromResult<User?>(id == user.Id ? user : null);
        }

        public Task<User?> FindForUpdateAsync(UserId id, CancellationToken cancellationToken)
        {
            ForUpdateReads++;
            return Task.FromResult<User?>(id == user.Id ? user : null);
        }

        public Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
            Instant now,
            int max,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UserId>>([]);

        public void Add(User added) => throw new NotSupportedException();
    }
}

public sealed class UnbanUserUseCaseTests
{
    [Fact]
    public async Task Execute_RestoresAccess_D180()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 7, 31, 14, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("BCDEFGHJKM"),
            AgeGateSource.SelfAttested,
            clock);
        user.Ban(clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var unitOfWork = new FakeUnitOfWork();
        var useCase = new UnbanUserUseCase(users, unitOfWork);

        AdminUserMutationResult result = await useCase.ExecuteAsync(user.Id, default);

        Assert.True(result.Succeeded);
        Assert.Null(user.BannedAt);
        Assert.Equal(1, unitOfWork.Executions);
    }
}

public sealed class ListUsersUseCaseTests
{
    [Fact]
    public async Task Execute_ReportsBanState_D179()
    {
        UserId id = UserId.New();
        Instant createdAt = Instant.FromUtc(2026, 7, 1, 12, 0);
        Instant bannedAt = Instant.FromUtc(2026, 7, 30, 12, 0);
        var reader = new FakeAdminUserReader
        {
            Page = new AdminUserPage([Record(id, createdAt, bannedAt)], 1),
        };
        var useCase = new ListUsersUseCase(reader);

        AdminUsersResult result = await useCase.ExecuteAsync(" player ", 1, 25, default);

        AdminUser user = Assert.Single(result.Users);
        Assert.Equal(id, user.Id);
        Assert.Equal("player", user.Username);
        Assert.Equal("Player One", user.DisplayName);
        Assert.Equal("o***@example.test", user.Email);
        Assert.Equal(createdAt, user.CreatedAt);
        Assert.Equal(UserLifecycle.Active, user.Lifecycle);
        Assert.Equal(bannedAt, user.BannedAt);
        Assert.Equal("player", reader.Query);
        Assert.Equal(1, result.Total);
    }

    /// <summary>
    /// D-179's per-user stats reach the projection unchanged — registered date, last activity, and
    /// the three counts — while the email stays masked. The reader's own SQL is proven against a real
    /// database by <c>AdminUserStatsTests</c>.
    /// </summary>
    [Fact]
    public async Task Execute_ProjectsEveryPerUserStat_D179()
    {
        UserId id = UserId.New();
        Instant createdAt = Instant.FromUtc(2026, 7, 1, 12, 0);
        Instant lastSeenAt = Instant.FromUtc(2026, 8, 11, 9, 30);
        var reader = new FakeAdminUserReader
        {
            Page = new AdminUserPage(
                [Record(id, createdAt, bannedAt: null, lastSeenAt, 3, 5, 7)],
                1),
        };

        AdminUsersResult listed = await new ListUsersUseCase(reader).ExecuteAsync(null, 1, 25, default);
        AdminUser? fetched = await new GetUserUseCase(reader).ExecuteAsync(id, default);

        Assert.NotNull(fetched);
        foreach (AdminUser user in new AdminUser[] { Assert.Single(listed.Users), fetched })
        {
            Assert.Equal(createdAt, user.CreatedAt);
            Assert.Equal(lastSeenAt, user.LastSeenAt);
            Assert.Equal(3, user.SessionsOrganized);
            Assert.Equal(5, user.SessionsJoined);
            Assert.Equal(7, user.FriendCount);
            Assert.Equal("o***@example.test", user.Email);
        }
    }

    [Fact]
    public async Task Execute_ReportsANeverSeenUserAsNullLastActivity_D179()
    {
        UserId id = UserId.New();
        var reader = new FakeAdminUserReader
        {
            Page = new AdminUserPage(
                [Record(id, Instant.FromUtc(2026, 7, 1, 12, 0), bannedAt: null, lastSeenAt: null)],
                1),
        };

        AdminUsersResult result = await new ListUsersUseCase(reader).ExecuteAsync(null, 1, 25, default);

        Assert.Null(Assert.Single(result.Users).LastSeenAt);
    }

    private static AdminUserRecord Record(
        UserId id,
        Instant createdAt,
        Instant? bannedAt,
        Instant? lastSeenAt = null,
        int sessionsOrganized = 0,
        int sessionsJoined = 0,
        int friendCount = 0) =>
        new(
            id,
            "player",
            "Player One",
            "owner@example.test",
            createdAt,
            UserLifecycle.Active,
            bannedAt,
            lastSeenAt,
            sessionsOrganized,
            sessionsJoined,
            friendCount);
}

public sealed class ReadDeletionJournalUseCaseTests
{
    [Fact]
    public async Task Execute_ReturnsTheRestoreReplayFeed()
    {
        Instant since = Instant.FromUtc(2026, 7, 1, 0, 0);
        var journal = new FakeDeletionJournal(
        [
            new DeletionJournalEntry(UserId.New(), since + Duration.FromHours(1)),
        ]);
        var useCase = new ReadDeletionJournalUseCase(journal);

        IReadOnlyList<DeletionJournalEntry> result = await useCase.ExecuteAsync(
            since,
            50,
            default);

        Assert.Equal(journal.Entries, result);
        Assert.Equal(since, journal.Since);
        Assert.Equal(50, journal.Limit);
    }

    private sealed class FakeDeletionJournal(IReadOnlyList<DeletionJournalEntry> entries)
        : IDeletionJournal
    {
        internal IReadOnlyList<DeletionJournalEntry> Entries { get; } = entries;

        internal Instant Since { get; private set; }

        internal int Limit { get; private set; }

        public void Append(DeletionJournalEntry entry) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeletionJournalEntry>> ReadSinceAsync(
            Instant since,
            int limit,
            CancellationToken cancellationToken)
        {
            Since = since;
            Limit = limit;
            return Task.FromResult(Entries);
        }
    }
}

internal sealed class FakeAdminUserReader : IAdminUserReader
{
    internal AdminUserPage Page { get; init; } = new([], 0);

    internal string? Query { get; private set; }

    public Task<AdminUserPage> ListAsync(
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Query = query;
        return Task.FromResult(Page);
    }

    public Task<AdminUserRecord?> GetAsync(
        UserId id,
        CancellationToken cancellationToken) =>
        Task.FromResult(Page.Users.SingleOrDefault(user => user.Id == id));
}
