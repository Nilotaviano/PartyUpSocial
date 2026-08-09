using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Notifications;

/// <summary>
/// PHASE7_DESIGN.md §1's S1 schema/migration gate: <c>push_subscriptions</c>,
/// <c>notification_push_sends</c>, <c>notifications.dispatch_state</c> (default Pending, backfilled to
/// Skipped on every pre-existing row), the poll-side mute unique and the origin-poll index -- every
/// DB-enforced uniqueness/FK proven by an actual violation (not by inventory presence alone), on the
/// <c>PollMigrationTests</c> precedent.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PushTransportMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_CreatesPushTransportTablesConstraintsAndIndexes_D112()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        string[] relations =
        [
            "push_subscriptions",
            "notification_push_sends",
            "ix_sessions_origin_poll_id",
            "ix_notifications_dispatch_pending",
            "ix_notifications_unread",
            "ux_notification_mutes_poll_user",
            "ux_notification_push_sends_once",
            "ux_push_subscriptions_active_endpoint",
        ];

        foreach (string relation in relations)
        {
            await AssertRelationExistsAsync(context, relation);
        }

        await AssertConstraintExistsAsync(context, "fk_push_subscriptions_users_user_id");
        await AssertConstraintExistsAsync(context, "fk_notification_push_sends_notifications_notification_id");
        await AssertConstraintExistsAsync(
            context,
            "fk_notification_push_sends_push_subscriptions_push_subscriptio");

        // §1: dispatch_state and channel are ordinary smallint columns, never derived.
        Assert.Equal("smallint", await ColumnTypeAsync(context, "notifications", "dispatch_state"));
        Assert.Equal("smallint", await ColumnTypeAsync(context, "push_subscriptions", "channel"));

        // §1 partial uniques/indexes: the WHERE half is load-bearing, so pin each filter in the live
        // index definition (Phase 6 PollMigrationTests precedent) rather than relying on duplicate-row
        // probes alone, which a plain unique index would also reject.
        await AssertIndexFilterAsync(context, "ux_push_subscriptions_active_endpoint", "revoked_at IS NULL");
        await AssertIndexFilterAsync(context, "ix_notifications_dispatch_pending", "dispatch_state = 0");
        await AssertIndexFilterAsync(context, "ix_notifications_unread", "read_at IS NULL");
        await AssertIndexFilterAsync(context, "ux_notification_mutes_poll_user", "poll_id IS NOT NULL");
        await AssertIndexFilterAsync(context, "ix_sessions_origin_poll_id", "origin_poll_id IS NOT NULL");

        // The DESC halves of the unread keyset index are as load-bearing as its WHERE half -- a
        // flipped direction silently degrades the S2 keyset page -- so the full column list is pinned,
        // not only the filter.
        Assert.Contains(
            "(recipient_user_id, created_at DESC, id DESC)",
            await IndexDefinitionAsync(context, "ix_notifications_unread"));
    }

    /// <summary>
    /// §1: dispatch_state defaults to Pending(0). S3's dispatch page reaches new rows only through
    /// this default -- were it ever wrong, ix_notifications_dispatch_pending would silently find
    /// nothing and push would never fire -- so a notification created through the production path
    /// (Notification.Create + SaveChanges) must read Pending.
    /// </summary>
    [Fact]
    public async Task FreshNotificationRowDefaultsToPending_D112()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId userId = await SeedUserAsync(context);
        Guid notificationId = await SeedNotificationAsync(context, userId, userId);

        short dispatchState = await context.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == NotificationId.From(notificationId))
            .Select(notification => EF.Property<short>(notification, "DispatchState"))
            .SingleAsync();
        Assert.Equal(0, dispatchState);
    }

    [Fact]
    public async Task DuplicateActivePushSubscriptionForSameEndpointIsRejected_D112()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId userId = await SeedUserAsync(context);
        await SeedSubscriptionAsync(context, userId, "wp-endpoint-1");
        const string endpoint = "https://push.example/wp-endpoint-1";
        string endpointHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(endpoint)));

        // A second row for the identical (user, channel, endpoint) inserted through raw SQL, so the
        // constraint -- not the repository's own find-first upsert check -- is what is under test.
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO push_subscriptions
                (id, user_id, endpoint, public_key, auth_secret, created_at, channel, endpoint_hash)
            VALUES
                ({Guid.CreateVersion7()}, {userId.Value}, {endpoint},
                 'public-key', 'auth-secret', now(), 0, {endpointHash})
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_push_subscriptions_active_endpoint", exception.ConstraintName);
    }

    [Fact]
    public async Task DanglingPushSubscriptionUserIdIsRejected_D112()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        Guid danglingUserId = Guid.CreateVersion7();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO push_subscriptions
                (id, user_id, endpoint, public_key, auth_secret, created_at, channel, endpoint_hash)
            VALUES
                ({Guid.CreateVersion7()}, {danglingUserId}, 'https://push.example/dangling',
                 'public-key', 'auth-secret', now(), 0, 'dangling-hash')
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_push_subscriptions_users_user_id", exception.ConstraintName);
    }

    [Fact]
    public async Task DuplicateNotificationPushSendClaimIsRejected_D112()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId userId = await SeedUserAsync(context);
        Guid notificationId = await SeedNotificationAsync(context, userId, userId);
        Guid subscriptionId = await SeedSubscriptionAsync(context, userId, "wp-endpoint-claim");

        await ExecuteAsync(context, $"""
            INSERT INTO notification_push_sends (id, notification_id, push_subscription_id, claimed_at, outcome)
            VALUES ({Guid.CreateVersion7()}, {notificationId}, {subscriptionId}, now(), 0)
            """);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO notification_push_sends (id, notification_id, push_subscription_id, claimed_at, outcome)
            VALUES ({Guid.CreateVersion7()}, {notificationId}, {subscriptionId}, now(), 0)
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_notification_push_sends_once", exception.ConstraintName);
    }

    [Fact]
    public async Task DanglingNotificationPushSendNotificationIdIsRejected_D112()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId userId = await SeedUserAsync(context);
        Guid subscriptionId = await SeedSubscriptionAsync(context, userId, "wp-endpoint-dangling-notification");
        Guid danglingNotificationId = Guid.CreateVersion7();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO notification_push_sends (id, notification_id, push_subscription_id, claimed_at, outcome)
            VALUES ({Guid.CreateVersion7()}, {danglingNotificationId}, {subscriptionId}, now(), 0)
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_notification_push_sends_notifications_notification_id", exception.ConstraintName);
    }

    [Fact]
    public async Task DanglingNotificationPushSendSubscriptionIdIsRejected_D112()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId userId = await SeedUserAsync(context);
        Guid notificationId = await SeedNotificationAsync(context, userId, userId);
        Guid danglingSubscriptionId = Guid.CreateVersion7();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO notification_push_sends (id, notification_id, push_subscription_id, claimed_at, outcome)
            VALUES ({Guid.CreateVersion7()}, {notificationId}, {danglingSubscriptionId}, now(), 0)
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal(
            "fk_notification_push_sends_push_subscriptions_push_subscriptio",
            exception.ConstraintName);
    }

    [Fact]
    public async Task DuplicatePollMuteForSameUserIsRejected_D113()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId organizerId = await SeedUserAsync(context);
        UserId muterId = await SeedUserAsync(context);
        Guid pollId = await SeedPollAsync(context, organizerId);

        await ExecuteAsync(context, $"""
            INSERT INTO notification_mutes (id, user_id, poll_id, created_at)
            VALUES ({Guid.CreateVersion7()}, {muterId.Value}, {pollId}, now())
            """);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO notification_mutes (id, user_id, poll_id, created_at)
            VALUES ({Guid.CreateVersion7()}, {muterId.Value}, {pollId}, now())
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_notification_mutes_poll_user", exception.ConstraintName);
    }

    /// <summary>
    /// §1: shipping Phase 7 must not replay months of history as a push storm, so the migration
    /// backfills every pre-existing row to the terminal Skipped(2) state -- proven by migrating to the
    /// pre-Phase-7 head, seeding rows the old way, then migrating forward and reading them back.
    /// </summary>
    [Fact]
    public async Task Migrate_BackfillsEveryPreExistingNotificationToSkipped_D112()
    {
        await using AppDbContext context = CreateContext();
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260808182728_AddPollDeadlineNoticeIndex");
        UserId recipientId = await SeedUserAsync(context);
        // Inserted via raw SQL against the pre-Phase-7 column set (mirroring
        // FriendGraphMigrationTests.Migrate_BackfillsBlockUsernameSnapshotFromCurrentUsername_D065):
        // the current AppDbContext model already carries dispatch_state, so a domain-object
        // SaveChanges here would generate an INSERT against a column the DB at this migration point
        // does not have yet.
        Guid preExistingId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO notifications (id, recipient_user_id, type, actor_user_id, created_at)
            VALUES ({preExistingId}, {recipientId.Value}, 23, {recipientId.Value}, now())
            """);

        await migrator.MigrateAsync();

        short dispatchState = await context.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == NotificationId.From(preExistingId))
            .Select(notification => EF.Property<short>(notification, "DispatchState"))
            .SingleAsync();
        Assert.Equal(2, dispatchState);

        long pendingCount = await ReadScalarLongAsync(
            context,
            "SELECT count(*) FROM notifications WHERE dispatch_state = 0");
        Assert.Equal(0, pendingCount);
    }

    [Fact]
    public async Task UpsertActiveAsync_RoundTripsASubscriptionAndWritesTheShadowHash_D112()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId userId = await SeedUserAsync(context);
        var repository = new PushSubscriptionRepository(context);
        const string endpoint = "https://push.example/round-trip";

        PushSubscriptionId firstId = await repository.UpsertActiveAsync(
            userId, endpoint, "public-key-1", "auth-secret-1", SystemClock.Instance, default);
        await context.SaveChangesAsync();

        // A second upsert for the identical (user, channel, endpoint) is idempotent: it returns the
        // same row rather than creating a second active one.
        await using AppDbContext secondContext = CreateContext();
        var secondRepository = new PushSubscriptionRepository(secondContext);
        PushSubscriptionId secondId = await secondRepository.UpsertActiveAsync(
            userId, endpoint, "public-key-2", "auth-secret-2", SystemClock.Instance, default);
        await secondContext.SaveChangesAsync();
        Assert.Equal(firstId, secondId);

        Assert.Equal(1, await context.PushSubscriptions.CountAsync(subscription => subscription.UserId == userId));

        await using AppDbContext verification = CreateContext();
        PushSubscription reloaded = await verification.PushSubscriptions
            .SingleAsync(subscription => subscription.Id == firstId);
        Assert.Equal(endpoint, reloaded.Endpoint);
        string expectedHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(endpoint)));
        string storedHash = await verification.PushSubscriptions
            .Where(subscription => subscription.Id == firstId)
            .Select(subscription => EF.Property<string>(subscription, "EndpointHash"))
            .SingleAsync();
        Assert.Equal(expectedHash, storedHash);
        short storedChannel = await verification.PushSubscriptions
            .Where(subscription => subscription.Id == firstId)
            .Select(subscription => EF.Property<short>(subscription, "Channel"))
            .SingleAsync();
        Assert.Equal(0, storedChannel);
    }

    [Fact]
    public async Task ClaimAsync_ADuplicateClaimReturnsNoRowAndExactlyOnePersists_D112()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId userId = await SeedUserAsync(context);
        Guid notificationId = await SeedNotificationAsync(context, userId, userId);
        Guid subscriptionId = await SeedSubscriptionAsync(context, userId, "wp-endpoint-claim-idempotent");
        var repository = new NotificationPushSendRepository(context);
        NotificationId typedNotificationId = NotificationId.From(notificationId);
        PushSubscriptionId typedSubscriptionId = PushSubscriptionId.From(subscriptionId);

        Guid? firstClaim = await repository.ClaimAsync(
            typedNotificationId, typedSubscriptionId, SystemClock.Instance, default);
        Guid? secondClaim = await repository.ClaimAsync(
            typedNotificationId, typedSubscriptionId, SystemClock.Instance, default);

        Assert.NotNull(firstClaim);
        Assert.Null(secondClaim);
        long sendCount = await ReadScalarLongAsync(
            context,
            "SELECT count(*) FROM notification_push_sends");
        Assert.Equal(1, sendCount);
    }

    private static async Task<Guid> SeedSubscriptionAsync(AppDbContext context, UserId userId, string endpointSuffix)
    {
        var repository = new PushSubscriptionRepository(context);
        PushSubscriptionId id = await repository.UpsertActiveAsync(
            userId,
            $"https://push.example/{endpointSuffix}",
            "public-key",
            "auth-secret",
            SystemClock.Instance,
            default);
        await context.SaveChangesAsync();
        return id.Value;
    }

    private static async Task<UserId> SeedUserAsync(AppDbContext context)
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create($"PU-{Guid.NewGuid():N}"[..9]),
            AgeGateSource.SelfAttested,
            SystemClock.Instance);
        context.DomainUsers.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> SeedNotificationAsync(
        AppDbContext context,
        UserId recipientId,
        UserId actorId)
    {
        Notification notification = Notification.Create(
            recipientId,
            NotificationType.FriendRequestReceived,
            NotificationSubject.ForActor(actorId),
            SystemClock.Instance);
        context.Notifications.Add(notification);
        await context.SaveChangesAsync();
        return notification.Id.Value;
    }

    private static async Task<Guid> SeedPollAsync(AppDbContext context, UserId organizerId)
    {
        Guid pollId = Guid.CreateVersion7();
        Instant deadline = Instant.FromUtc(2026, 9, 2, 12, 0);
        await ExecuteAsync(context, $"""
            INSERT INTO polls
                (id, organizer_id, title, duration_minutes, platform, notes, min_attendance,
                 max_capacity, is_private, deadline_choice, deadline, status, created_at, selection_window_end)
            VALUES
                ({pollId}, {organizerId.Value}, 'Mute boundary test', 60, 3, '', 1, NULL, true, 2,
                 {deadline}, 0, {deadline - Duration.FromHours(24)}, {deadline + Duration.FromHours(48)})
            """);
        return pollId;
    }

    private static async Task ExecuteAsync(AppDbContext context, FormattableString sql) =>
        await context.Database.ExecuteSqlInterpolatedAsync(sql);

    private static async Task<long> ReadScalarLongAsync(AppDbContext context, string commandText)
    {
        DbConnection connection = context.Database.GetDbConnection();
        await EnsureOpenAsync(connection);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }

    private static async Task AssertRelationExistsAsync(AppDbContext context, string relation)
    {
        DbConnection connection = context.Database.GetDbConnection();
        await EnsureOpenAsync(connection);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('public.' || @relation)::text";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "relation";
        parameter.Value = relation;
        command.Parameters.Add(parameter);

        Assert.Equal(relation, await command.ExecuteScalarAsync());
    }

    private static async Task AssertIndexFilterAsync(AppDbContext context, string index, string filterFragment)
    {
        string definition = await IndexDefinitionAsync(context, index);
        Assert.Contains("WHERE", definition);
        Assert.Contains(filterFragment, definition);
    }

    private static async Task<string> IndexDefinitionAsync(AppDbContext context, string index)
    {
        DbConnection connection = context.Database.GetDbConnection();
        await EnsureOpenAsync(connection);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT pg_get_indexdef(to_regclass('public.' || @index)::regclass)";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "index";
        parameter.Value = index;
        command.Parameters.Add(parameter);

        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ColumnTypeAsync(AppDbContext context, string table, string column)
    {
        DbConnection connection = context.Database.GetDbConnection();
        await EnsureOpenAsync(connection);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT format_type(atttypid, atttypmod)
            FROM pg_attribute
            WHERE attrelid = to_regclass('public.' || @table)
              AND attname = @column
              AND NOT attisdropped
            """;
        DbParameter tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);
        DbParameter columnParameter = command.CreateParameter();
        columnParameter.ParameterName = "column";
        columnParameter.Value = column;
        command.Parameters.Add(columnParameter);

        object? result = await command.ExecuteScalarAsync();
        return result as string;
    }

    private static async Task AssertConstraintExistsAsync(AppDbContext context, string constraint)
    {
        DbConnection connection = context.Database.GetDbConnection();
        await EnsureOpenAsync(connection);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT conname FROM pg_constraint WHERE conname = @constraint";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "constraint";
        parameter.Value = constraint;
        command.Parameters.Add(parameter);

        Assert.Equal(constraint, await command.ExecuteScalarAsync());
    }

    private static async Task EnsureOpenAsync(DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
    }
}
