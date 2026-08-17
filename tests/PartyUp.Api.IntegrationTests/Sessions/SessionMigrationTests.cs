using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// S1's schema/migration gate (PHASE4_DESIGN.md §4.3): one migration creates every Phase 4 table,
/// check, FK, explicit index and the <c>xmin</c> concurrency token, and a handful of shapes fail only
/// at the database boundary, not merely in application code.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_CreatesSessionFamilyConstraintsAndIndexes_D005()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        string[] relations =
        [
            "sessions",
            "session_participations",
            "join_requests",
            "guest_identities",
            "guest_links",
            "notification_mutes",
            "session_reminder_handoffs",
            "ix_sessions_organizer_status_start",
            "ix_sessions_status_start_id",
            "ux_session_participations_session_user",
            "ux_session_participations_session_guest",
            "ix_session_participations_user_status_session",
            "ix_session_participations_session_status_waitlist",
            "ix_join_requests_session_status_created_id",
            "ux_join_requests_session_pending_requester",
            "ux_guest_links_token_hash",
            "ix_guest_links_session_state",
            "ux_notification_mutes_session_user",
            "ux_session_reminder_handoffs_session_recipient",
            "ix_session_reminder_handoffs_state_due_id",
            "ux_notifications_session_reminder_once",
        ];

        foreach (string relation in relations)
        {
            await AssertRelationExistsAsync(context, relation);
        }

        await AssertConstraintExistsAsync(context, "ck_sessions_duration");
        await AssertConstraintExistsAsync(context, "ck_sessions_min_attendance");
        await AssertConstraintExistsAsync(context, "ck_sessions_capacity");
        await AssertConstraintExistsAsync(context, "ck_sessions_game_shape");
        await AssertConstraintExistsAsync(context, "ck_session_participations_participant");
        await AssertConstraintExistsAsync(context, "ck_session_participations_reminder");
        await AssertConstraintExistsAsync(context, "ck_join_requests_target");
        await AssertConstraintExistsAsync(context, "ck_guest_links_target");
        await AssertConstraintExistsAsync(context, "ck_guest_identities_target");
        await AssertConstraintExistsAsync(context, "ck_notification_mutes_target");
        await AssertConstraintExistsAsync(context, "ck_session_reminder_handoffs_delivered");
        await AssertConstraintExistsAsync(context, "fk_sessions_users_organizer_id");
        await AssertConstraintExistsAsync(context, "fk_session_participations_sessions_session_id");
        await AssertConstraintExistsAsync(context, "fk_notifications_session_reminder_handoffs_reminder_handoff_id");

        // §4.2: only `sessions` maps xmin as a concurrency token. Postgres gives every table a hidden
        // xmin system column regardless, so its mere presence proves nothing — the EF model itself is
        // the fact under test: only Session's entity type carries a concurrency-token property.
        Assert.Contains(
            context.Model.FindEntityType(typeof(PartyUp.Domain.Sessions.Session))!.GetProperties(),
            property => property.IsConcurrencyToken);
        Assert.DoesNotContain(
            context.Model.FindEntityType(typeof(PartyUp.Domain.Sessions.Participation))!.GetProperties(),
            property => property.IsConcurrencyToken);
        Assert.DoesNotContain(
            context.Model.FindEntityType(typeof(PartyUp.Domain.Sessions.JoinRequest))!.GetProperties(),
            property => property.IsConcurrencyToken);
        Assert.DoesNotContain(
            context.Model.FindEntityType(typeof(PartyUp.Domain.Sessions.GuestLink))!.GetProperties(),
            property => property.IsConcurrencyToken);

        // No end_time column and no expression index over a computed end (§4.1).
        Assert.Null(await ColumnTypeAsync(context, "sessions", "end_time"));
    }

    [Fact]
    public async Task UnlimitedCapacityAndWaitlistShapeAreConstrained_D091()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);

        // D-091: null max_capacity (unlimited) is a legal shape.
        Guid unlimitedId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO sessions
                (id, organizer_id, title, start_time, duration_minutes, platform, notes,
                 min_attendance, max_capacity, visibility, status, created_at)
            VALUES
                ({unlimitedId}, {organizerId.Value}, 'Unlimited', now() + interval '1 day', 60, 3, '',
                 1, NULL, 1, 0, now())
            """);

        // D-091's waitlist shape: `is_waitlisted` is an ordinary boolean column with no database-level
        // tie to `max_capacity` — the schema stores whatever the application decided, and the waitlist
        // rule itself is Domain (`Session.HasFreeSlot`), not a database constraint. What the boundary
        // does own is that the column round-trips both values faithfully.
        UserId waitlistedUserId = await SeedUserAsync(context);
        Guid waitlistedParticipationId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO session_participations
                (id, session_id, user_id, status, is_waitlisted, origin, created_at)
            VALUES
                ({waitlistedParticipationId}, {unlimitedId}, {waitlistedUserId.Value}, 1, true, 0, now())
            """);

        UserId activeUserId = await SeedUserAsync(context);
        Guid activeParticipationId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO session_participations
                (id, session_id, user_id, status, is_waitlisted, origin, created_at)
            VALUES
                ({activeParticipationId}, {unlimitedId}, {activeUserId.Value}, 1, false, 0, now())
            """);

        Assert.True(await IsWaitlistedAsync(context, waitlistedParticipationId));
        Assert.False(await IsWaitlistedAsync(context, activeParticipationId));

        // An invalid capacity — max below min — fails at the database boundary (ck_sessions_capacity).
        Guid invalidId = Guid.CreateVersion7();
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO sessions
                (id, organizer_id, title, start_time, duration_minutes, platform, notes,
                 min_attendance, max_capacity, visibility, status, created_at)
            VALUES
                ({invalidId}, {organizerId.Value}, 'Invalid', now() + interval '1 day', 60, 3, '',
                 4, 2, 1, 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_sessions_capacity", exception.ConstraintName);
    }

    [Fact]
    public async Task DuplicateGuestLinkTokenHashIsRejected_D005()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        Guid sessionId = await InsertSessionAsync(context, organizerId);

        const string tokenHash = "duplicate-token-hash-digest-000000000000";
        await ExecuteAsync(context, $"""
            INSERT INTO guest_links (id, session_id, token_hash, state, created_at)
            VALUES ({Guid.CreateVersion7()}, {sessionId}, {tokenHash}, 0, now())
            """);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO guest_links (id, session_id, token_hash, state, created_at)
            VALUES ({Guid.CreateVersion7()}, {sessionId}, {tokenHash}, 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_guest_links_token_hash", exception.ConstraintName);
    }

    [Fact]
    public async Task DuplicateSessionParticipantIsRejected_D005()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        UserId participantId = await SeedUserAsync(context);
        Guid sessionId = await InsertSessionAsync(context, organizerId);

        await ExecuteAsync(context, $"""
            INSERT INTO session_participations (id, session_id, user_id, status, is_waitlisted, origin, created_at)
            VALUES ({Guid.CreateVersion7()}, {sessionId}, {participantId.Value}, 0, false, 0, now())
            """);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO session_participations (id, session_id, user_id, status, is_waitlisted, origin, created_at)
            VALUES ({Guid.CreateVersion7()}, {sessionId}, {participantId.Value}, 0, false, 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_session_participations_session_user", exception.ConstraintName);
    }

    [Fact]
    public async Task GuestLinkSessionXorPollIsRejected_D005()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        Guid sessionId = await InsertSessionAsync(context, organizerId);

        PostgresException bothNull = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO guest_links (id, session_id, poll_id, token_hash, state, created_at)
            VALUES ({Guid.CreateVersion7()}, NULL, NULL, 'both-null-digest', 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, bothNull.SqlState);
        Assert.Equal("ck_guest_links_target", bothNull.ConstraintName);

        PostgresException bothSet = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO guest_links (id, session_id, poll_id, token_hash, state, created_at)
            VALUES ({Guid.CreateVersion7()}, {sessionId}, {Guid.CreateVersion7()}, 'both-set-digest', 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, bothSet.SqlState);
        Assert.Equal("ck_guest_links_target", bothSet.ConstraintName);
    }

    [Fact]
    public async Task JoinRequestSessionXorPollIsRejected_D005()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        UserId requesterId = await SeedUserAsync(context);
        Guid sessionId = await InsertSessionAsync(context, organizerId);

        PostgresException bothNull = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO join_requests (id, session_id, poll_id, requester_user_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, NULL, NULL, {requesterId.Value}, 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, bothNull.SqlState);
        Assert.Equal("ck_join_requests_target", bothNull.ConstraintName);

        PostgresException bothSet = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO join_requests (id, session_id, poll_id, requester_user_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, {sessionId}, {Guid.CreateVersion7()}, {requesterId.Value}, 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, bothSet.SqlState);
        Assert.Equal("ck_join_requests_target", bothSet.ConstraintName);
    }

    [Fact]
    public async Task NotificationMuteSessionXorPollIsRejected_D005()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        UserId mutingUserId = await SeedUserAsync(context);
        Guid sessionId = await InsertSessionAsync(context, organizerId);

        PostgresException bothNull = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO notification_mutes (id, user_id, session_id, poll_id, created_at)
            VALUES ({Guid.CreateVersion7()}, {mutingUserId.Value}, NULL, NULL, now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, bothNull.SqlState);
        Assert.Equal("ck_notification_mutes_target", bothNull.ConstraintName);

        PostgresException bothSet = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO notification_mutes (id, user_id, session_id, poll_id, created_at)
            VALUES ({Guid.CreateVersion7()}, {mutingUserId.Value}, {sessionId}, {Guid.CreateVersion7()}, now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, bothSet.SqlState);
        Assert.Equal("ck_notification_mutes_target", bothSet.ConstraintName);
    }

    [Fact]
    public async Task SecondReminderNotificationForOneHandoffIsRejected_D082()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        Guid sessionId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO sessions
                (id, organizer_id, title, start_time, duration_minutes, platform, notes,
                 min_attendance, max_capacity, visibility, status, created_at)
            VALUES
                ({sessionId}, {organizerId.Value}, 'Reminder test', now() + interval '1 day', 60, 3, '',
                 1, NULL, 1, 0, now())
            """);

        Guid handoffId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO session_reminder_handoffs
                (id, session_id, recipient_user_id, due_at, state, planned_at, version, attempts)
            VALUES
                ({handoffId}, {sessionId}, {organizerId.Value}, now() + interval '23 hours', 0, now(), 0, 0)
            """);

        await ExecuteAsync(context, $"""
            INSERT INTO notifications (id, recipient_user_id, type, session_id, created_at, reminder_handoff_id)
            VALUES ({Guid.CreateVersion7()}, {organizerId.Value}, 12, {sessionId}, now(), {handoffId})
            """);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO notifications (id, recipient_user_id, type, session_id, created_at, reminder_handoff_id)
            VALUES ({Guid.CreateVersion7()}, {organizerId.Value}, 12, {sessionId}, now(), {handoffId})
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_notifications_session_reminder_once", exception.ConstraintName);
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

    private static async Task<Guid> InsertSessionAsync(AppDbContext context, UserId organizerId)
    {
        Guid sessionId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO sessions
                (id, organizer_id, title, start_time, duration_minutes, platform, notes,
                 min_attendance, max_capacity, visibility, status, created_at)
            VALUES
                ({sessionId}, {organizerId.Value}, 'Boundary test', now() + interval '1 day', 60, 3, '',
                 1, NULL, 1, 0, now())
            """);
        return sessionId;
    }

    private static async Task ExecuteAsync(AppDbContext context, FormattableString sql) =>
        await context.Database.ExecuteSqlInterpolatedAsync(sql);

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

    private static async Task<bool> IsWaitlistedAsync(AppDbContext context, Guid participationId)
    {
        DbConnection connection = context.Database.GetDbConnection();
        await EnsureOpenAsync(connection);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT is_waitlisted FROM session_participations WHERE id = @id";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = participationId;
        command.Parameters.Add(parameter);

        return (bool)(await command.ExecuteScalarAsync())!;
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

    private static async Task EnsureOpenAsync(DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
    }
}
