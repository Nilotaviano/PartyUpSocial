using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendGraphMigrationTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();

    public Task InitializeAsync() => _database.StartAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task Migrate_CreatesEveryConstraintAndIndex_D015()
    {
        await using AppDbContext context = _database.CreateContext();
        await context.Database.MigrateAsync();

        string[] actual = await FriendGraphDatabase.ReadStringsAsync(
            context,
            """
            SELECT 'constraint:' || conname
            FROM pg_constraint
            WHERE conrelid IN ('blocks'::regclass, 'friendships'::regclass, 'notifications'::regclass)
            UNION ALL
            SELECT 'index:' || indexname
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename IN ('blocks', 'friendships', 'notifications')
            ORDER BY 1
            """);

        string[] expected =
        [
            "constraint:ck_blocks_not_self",
            "constraint:ck_friendships_accept_time",
            "constraint:ck_friendships_canonical",
            "constraint:ck_friendships_requester",
            "constraint:ck_friendships_status",
            "constraint:ck_notifications_friend_shape",
            "constraint:ck_notifications_type",
            "constraint:fk_blocks_users_blocked_id",
            "constraint:fk_blocks_users_blocker_id",
            "constraint:fk_friendships_users_requested_by",
            "constraint:fk_friendships_users_user_hi_id",
            "constraint:fk_friendships_users_user_lo_id",
            "constraint:fk_notifications_polls_poll_id",
            "constraint:fk_notifications_session_reminder_handoffs_reminder_handoff_id",
            "constraint:fk_notifications_users_actor_user_id",
            "constraint:fk_notifications_users_recipient_user_id",
            "constraint:pk_blocks",
            "constraint:pk_friendships",
            "constraint:pk_notifications",
            "constraint:ux_blocks_ordered_pair",
            "constraint:ux_friendships_pair",
            "index:ix_blocks_blocked",
            "index:ix_blocks_blocker_created",
            "index:ix_friendships_hi_status",
            "index:ix_friendships_lo_status",
            "index:ix_friendships_pending_requested_by",
            // PHASE7_DESIGN.md §1 (S1): dispatch_state's partial index and the unread keyset index --
            // both land on notifications, so this closed inventory gains exactly these two entries.
            "index:ix_notifications_dispatch_pending",
            "index:ix_notifications_recipient_created",
            "index:ix_notifications_unread",
            "index:pk_blocks",
            "index:pk_friendships",
            "index:pk_notifications",
            "index:ux_blocks_ordered_pair",
            "index:ux_friendships_pair",
            "index:ux_notifications_poll_deadline_once",
            "index:ux_notifications_session_reminder_once",
            "index:ux_notifications_steam_match_once",
        ];
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Migrate_FromCurrentHeadAppliesFriendGraph_D015()
    {
        await using AppDbContext context = _database.CreateContext();
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260801002650_AddAdminSeedRefusals");

        Assert.Null(await FriendGraphDatabase.ReadScalarAsync(
            context,
            "SELECT to_regclass('public.friendships')::text"));

        await migrator.MigrateAsync();

        Assert.Equal(
            "friendships",
            await FriendGraphDatabase.ReadScalarAsync(
                context,
                "SELECT to_regclass('public.friendships')::text"));
    }

    [Fact]
    public async Task Migrate_BackfillsBlockUsernameSnapshotFromCurrentUsername_D065()
    {
        await using AppDbContext context = _database.CreateContext();
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260801172308_AddFriendGraph");
        var clock = new MutableClock(Instant.FromUtc(2026, 8, 2, 12, 0));
        User blocker = FriendGraphDatabase.CreateActiveUser(clock);
        User blocked = FriendGraphDatabase.CreateActiveUser(clock);
        context.DomainUsers.AddRange(blocker, blocked);
        await context.SaveChangesAsync();
        Block block = Block.Create(
            blocker.Id,
            blocked.Id,
            blocked.Username!.Value,
            clock);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO blocks (id, blocker_id, blocked_id, created_at)
            VALUES ({block.Id.Value}, {block.BlockerId.Value}, {block.BlockedId.Value},
                    {block.CreatedAt})
            """);

        await migrator.MigrateAsync();

        Assert.Equal(
            blocked.Username!.Value.Value,
            await FriendGraphDatabase.ReadScalarAsync(
                context,
                "SELECT blocked_username_snapshot FROM blocks"));
    }
}

internal sealed class FriendGraphDatabase
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    internal Task StartAsync() => _postgres.StartAsync();

    internal Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    internal AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }

    internal async Task<User[]> SeedUsersAsync(int count, IClock clock)
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        User[] users = Enumerable.Range(0, count)
            .Select(_ => CreateActiveUser(clock))
            .ToArray();
        context.DomainUsers.AddRange(users);
        await context.SaveChangesAsync();
        return users;
    }

    internal static User CreateActiveUser(IClock clock)
    {
        UserId id = UserId.New();
        string suffix = Guid.NewGuid().ToString("N")[..12];
        User user = User.BeginOnboarding(
            id,
            FriendCode.Create($"FG{suffix}"),
            AgeGateSource.SelfAttested,
            clock);
        user.CompleteOnboarding(
            Username.Create($"friend{suffix}"),
            DisplayName.Create($"Friend {suffix}"),
            Avatar.Fallback,
            DateTimeZone.Utc,
            Locale.Create("en"),
            clock);
        return user;
    }

    internal static async Task<string[]> ReadStringsAsync(
        AppDbContext context,
        string commandText)
    {
        DbConnection connection = context.Database.GetDbConnection();
        await EnsureOpenAsync(connection);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    internal static async Task<object?> ReadScalarAsync(
        AppDbContext context,
        string commandText)
    {
        DbConnection connection = context.Database.GetDbConnection();
        await EnsureOpenAsync(connection);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    internal static async Task EnsureOpenAsync(DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
    }
}
