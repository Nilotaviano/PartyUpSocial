using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// PHASE6_DESIGN.md §1, S1's schema/migration gate: one migration creates every poll table, its
/// checks, FKs and indexes, the retro FKs onto <c>sessions</c>/<c>notifications</c>/<c>guest_links</c>/
/// <c>guest_identities</c>, and the poll-side join-request partial unique — and every DB-enforced
/// uniqueness/FK in §1 fails only at the database boundary, proven by an actual violation
/// (not by inventory presence alone).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_CreatesPollFamilyConstraintsAndIndexes_D026()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        string[] relations =
        [
            "polls",
            "poll_options",
            "poll_participants",
            "poll_votes",
            "ix_polls_organizer_status_deadline",
            "ix_polls_status_deadline_id",
            "ux_poll_options",
            "ux_poll_participants_poll_user",
            "ux_poll_participants_poll_guest",
            "ux_poll_votes_option_user",
            "ux_poll_votes_option_guest",
            "ux_join_requests_poll_pending_requester",
        ];

        foreach (string relation in relations)
        {
            await AssertRelationExistsAsync(context, relation);
        }

        await AssertConstraintExistsAsync(context, "ck_polls_duration");
        await AssertConstraintExistsAsync(context, "ck_polls_min_attendance");
        await AssertConstraintExistsAsync(context, "ck_polls_capacity");
        await AssertConstraintExistsAsync(context, "ck_polls_game_shape");
        await AssertConstraintExistsAsync(context, "ck_poll_participants_participant");
        await AssertConstraintExistsAsync(context, "ck_poll_votes_participant");
        await AssertConstraintExistsAsync(context, "fk_polls_users_organizer_id");
        await AssertConstraintExistsAsync(context, "fk_poll_options_polls_poll_id");
        await AssertConstraintExistsAsync(context, "fk_poll_participants_polls_poll_id");
        await AssertConstraintExistsAsync(context, "fk_poll_votes_polls_poll_id");
        await AssertConstraintExistsAsync(context, "fk_poll_votes_poll_options_option_id");
        await AssertConstraintExistsAsync(context, "fk_sessions_polls_origin_poll_id");
        await AssertConstraintExistsAsync(context, "fk_notifications_polls_poll_id");
        await AssertConstraintExistsAsync(context, "fk_guest_links_polls_poll_id");
        await AssertConstraintExistsAsync(context, "fk_guest_identities_polls_poll_id");
        await AssertConstraintExistsAsync(context, "fk_join_requests_polls_poll_id");

        // §1: status is a materialized column the boundary stores and round-trips, never derives.
        Assert.Equal("smallint", await ColumnTypeAsync(context, "polls", "status"));
        Assert.Equal(
            "timestamp with time zone",
            await ColumnTypeAsync(context, "polls", "selection_window_end"));

        // §1 partial uniques: the WHERE half is load-bearing — the duplicate-row probes above use
        // only non-null values, which a plain unique index would also reject — so pin each filter
        // in the live index definition.
        await AssertIndexFilterAsync(context, "ux_poll_participants_poll_user", "user_id IS NOT NULL");
        await AssertIndexFilterAsync(context, "ux_poll_participants_poll_guest", "guest_identity_id IS NOT NULL");
        await AssertIndexFilterAsync(context, "ux_poll_votes_option_user", "user_id IS NOT NULL");
        await AssertIndexFilterAsync(context, "ux_poll_votes_option_guest", "guest_identity_id IS NOT NULL");
        await AssertIndexFilterAsync(context, "ux_join_requests_poll_pending_requester", "poll_id IS NOT NULL");
        await AssertIndexFilterAsync(context, "ux_join_requests_poll_pending_requester", "status = 0");
    }

    [Fact]
    public async Task NonPendingPollJoinRequestDoesNotBlockANewPendingOne_D118()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        UserId requesterId = await SeedUserAsync(context);
        Guid pollId = await InsertPollAsync(context, organizerId);

        // §5.3 "one OPEN request per (target, user)": Lapsed (4) and Rejected (2) rows must not
        // block a fresh Pending (0) row — the status = 0 half of the filter is what allows this.
        await ExecuteAsync(context, $"""
            INSERT INTO join_requests (id, poll_id, requester_user_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {requesterId.Value}, 4, now())
            """);
        await ExecuteAsync(context, $"""
            INSERT INTO join_requests (id, poll_id, requester_user_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {requesterId.Value}, 2, now())
            """);
        await ExecuteAsync(context, $"""
            INSERT INTO join_requests (id, poll_id, requester_user_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {requesterId.Value}, 0, now())
            """);

        Assert.Equal(3, await CountJoinRequestsAsync(context, pollId, requesterId));
    }

    [Fact]
    public async Task SelectionWindowEndIsAStoredColumnNotDerivedInQuery_D088()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        Instant deadline = Instant.FromUtc(2026, 9, 1, 0, 0);
        // Deliberately NOT deadline + 48h (D-088): if selection_window_end were a generated/computed
        // column, this insert would either be rejected or silently overwritten. Storing exactly what
        // was written proves it is an ordinary column (§1: "never derived in queries").
        Instant divergentEnd = deadline + Duration.FromHours(40);

        Guid pollId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO polls
                (id, organizer_id, title, duration_minutes, platform, notes, min_attendance,
                 max_capacity, is_private, deadline_choice, deadline, status, created_at, selection_window_end)
            VALUES
                ({pollId}, {organizerId.Value}, 'Divergent window', 60, 3, '', 1, NULL, true, 3,
                 {deadline}, 0, {deadline}, {divergentEnd})
            """);

        Instant stored = await ReadSelectionWindowEndAsync(context, pollId);
        Assert.Equal(divergentEnd, stored);
        Assert.NotEqual(PollSelectionWindow.EndsAt(deadline), stored);
    }

    [Fact]
    public async Task DuplicatePollOptionStartTimeIsRejected_D080()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        Guid pollId = await InsertPollAsync(context, organizerId);
        Instant startTime = Instant.FromUtc(2026, 9, 2, 18, 0);

        await InsertOptionAsync(context, Guid.CreateVersion7(), pollId, startTime);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertOptionAsync(context, Guid.CreateVersion7(), pollId, startTime));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_poll_options", exception.ConstraintName);
    }

    [Fact]
    public async Task DuplicatePollParticipantUserIsRejected_D026()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        UserId participantId = await SeedUserAsync(context);
        Guid pollId = await InsertPollAsync(context, organizerId);

        await ExecuteAsync(context, $"""
            INSERT INTO poll_participants (id, poll_id, user_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {participantId.Value}, 0, now())
            """);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO poll_participants (id, poll_id, user_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {participantId.Value}, 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_poll_participants_poll_user", exception.ConstraintName);
    }

    [Fact]
    public async Task DuplicatePollParticipantGuestIsRejected_D026()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        Guid pollId = await InsertPollAsync(context, organizerId);
        Guid guestIdentityId = await InsertGuestIdentityAsync(context, pollId);

        await ExecuteAsync(context, $"""
            INSERT INTO poll_participants (id, poll_id, guest_identity_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {guestIdentityId}, 0, now())
            """);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO poll_participants (id, poll_id, guest_identity_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {guestIdentityId}, 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_poll_participants_poll_guest", exception.ConstraintName);
    }

    [Fact]
    public async Task DuplicatePollVoteUserIsRejected_D026()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        UserId voterId = await SeedUserAsync(context);
        Guid pollId = await InsertPollAsync(context, organizerId);
        Guid optionId = await InsertOptionAsync(context, Guid.CreateVersion7(), pollId, Instant.FromUtc(2026, 9, 3, 18, 0));

        await ExecuteAsync(context, $"""
            INSERT INTO poll_votes (id, poll_id, option_id, user_id, choice, submitted_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {optionId}, {voterId.Value}, 0, now(), now())
            """);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO poll_votes (id, poll_id, option_id, user_id, choice, submitted_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {optionId}, {voterId.Value}, 1, now(), now())
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_poll_votes_option_user", exception.ConstraintName);
    }

    [Fact]
    public async Task DuplicatePollVoteGuestIsRejected_D026()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        Guid pollId = await InsertPollAsync(context, organizerId);
        Guid optionId = await InsertOptionAsync(context, Guid.CreateVersion7(), pollId, Instant.FromUtc(2026, 9, 3, 19, 0));
        Guid guestIdentityId = await InsertGuestIdentityAsync(context, pollId);

        await ExecuteAsync(context, $"""
            INSERT INTO poll_votes (id, poll_id, option_id, guest_identity_id, choice, submitted_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {optionId}, {guestIdentityId}, 0, now(), now())
            """);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO poll_votes (id, poll_id, option_id, guest_identity_id, choice, submitted_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {optionId}, {guestIdentityId}, 2, now(), now())
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_poll_votes_option_guest", exception.ConstraintName);
    }

    [Fact]
    public async Task DuplicatePendingPollJoinRequestIsRejected_D118()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        UserId requesterId = await SeedUserAsync(context);
        Guid pollId = await InsertPollAsync(context, organizerId);

        await ExecuteAsync(context, $"""
            INSERT INTO join_requests (id, poll_id, requester_user_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {requesterId.Value}, 0, now())
            """);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO join_requests (id, poll_id, requester_user_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, {pollId}, {requesterId.Value}, 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_join_requests_poll_pending_requester", exception.ConstraintName);
    }

    [Fact]
    public async Task DanglingOriginPollIdOnSessionIsRejected_D160()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId organizerId = await SeedUserAsync(context);
        Guid danglingPollId = Guid.CreateVersion7();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO sessions
                (id, organizer_id, title, start_time, duration_minutes, platform, notes,
                 min_attendance, max_capacity, is_private, origin_poll_id, status, created_at)
            VALUES
                ({Guid.CreateVersion7()}, {organizerId.Value}, 'Dangling origin', now() + interval '1 day', 60, 3, '',
                 1, NULL, true, {danglingPollId}, 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_sessions_polls_origin_poll_id", exception.ConstraintName);
    }

    [Fact]
    public async Task DanglingNotificationPollIdIsRejected_D120()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId recipientId = await SeedUserAsync(context);
        Guid danglingPollId = Guid.CreateVersion7();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO notifications (id, recipient_user_id, type, poll_id, created_at)
            VALUES ({Guid.CreateVersion7()}, {recipientId.Value}, 0, {danglingPollId}, now())
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_notifications_polls_poll_id", exception.ConstraintName);
    }

    [Fact]
    public async Task DanglingGuestLinkPollIdIsRejected_D048()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        Guid danglingPollId = Guid.CreateVersion7();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO guest_links (id, poll_id, token_hash, state, created_at)
            VALUES ({Guid.CreateVersion7()}, {danglingPollId}, 'dangling-poll-link-digest', 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_guest_links_polls_poll_id", exception.ConstraintName);
    }

    [Fact]
    public async Task DanglingGuestIdentityPollIdIsRejected_D048()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        Guid danglingPollId = Guid.CreateVersion7();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO guest_identities (id, poll_id, display_name, created_at)
            VALUES ({Guid.CreateVersion7()}, {danglingPollId}, 'Guest', now())
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_guest_identities_polls_poll_id", exception.ConstraintName);
    }

    [Fact]
    public async Task DanglingJoinRequestPollIdIsRejected_D118()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        UserId requesterId = await SeedUserAsync(context);
        Guid danglingPollId = Guid.CreateVersion7();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO join_requests (id, poll_id, requester_user_id, status, created_at)
            VALUES ({Guid.CreateVersion7()}, {danglingPollId}, {requesterId.Value}, 0, now())
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_join_requests_polls_poll_id", exception.ConstraintName);
    }

    private static async Task<Guid> InsertPollAsync(AppDbContext context, UserId organizerId)
    {
        Guid pollId = Guid.CreateVersion7();
        Instant deadline = Instant.FromUtc(2026, 9, 2, 12, 0);
        await ExecuteAsync(context, $"""
            INSERT INTO polls
                (id, organizer_id, title, duration_minutes, platform, notes, min_attendance,
                 max_capacity, is_private, deadline_choice, deadline, status, created_at, selection_window_end)
            VALUES
                ({pollId}, {organizerId.Value}, 'Boundary test', 60, 3, '', 1, NULL, true, 2,
                 {deadline}, 0, {deadline - Duration.FromHours(24)}, {PollSelectionWindow.EndsAt(deadline)})
            """);
        return pollId;
    }

    private static async Task<Guid> InsertOptionAsync(AppDbContext context, Guid optionId, Guid pollId, Instant startTime)
    {
        await ExecuteAsync(context, $"""
            INSERT INTO poll_options (id, poll_id, start_time, created_at)
            VALUES ({optionId}, {pollId}, {startTime}, now())
            """);
        return optionId;
    }

    private static async Task<Guid> InsertGuestIdentityAsync(AppDbContext context, Guid pollId)
    {
        Guid guestIdentityId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO guest_identities (id, poll_id, display_name, created_at)
            VALUES ({guestIdentityId}, {pollId}, 'Guest', now())
            """);
        return guestIdentityId;
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

    private static async Task ExecuteAsync(AppDbContext context, FormattableString sql) =>
        await context.Database.ExecuteSqlInterpolatedAsync(sql);

    private static async Task<Instant> ReadSelectionWindowEndAsync(AppDbContext context, Guid pollId) =>
        await context.Set<Poll>()
            .AsNoTracking()
            .Where(poll => poll.Id == PollId.From(pollId))
            .Select(poll => EF.Property<Instant>(poll, "SelectionWindowEnd"))
            .SingleAsync();

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
        DbConnection connection = context.Database.GetDbConnection();
        await EnsureOpenAsync(connection);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT pg_get_indexdef(to_regclass('public.' || @index)::regclass)";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "index";
        parameter.Value = index;
        command.Parameters.Add(parameter);

        string definition = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.Contains("WHERE", definition);
        Assert.Contains(filterFragment, definition);
    }

    private static async Task<long> CountJoinRequestsAsync(AppDbContext context, Guid pollId, UserId requesterId)
    {
        DbConnection connection = context.Database.GetDbConnection();
        await EnsureOpenAsync(connection);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM join_requests WHERE poll_id = @poll AND requester_user_id = @requester";
        DbParameter pollParameter = command.CreateParameter();
        pollParameter.ParameterName = "poll";
        pollParameter.Value = pollId;
        command.Parameters.Add(pollParameter);
        DbParameter requesterParameter = command.CreateParameter();
        requesterParameter.ParameterName = "requester";
        requesterParameter.Value = requesterId.Value;
        command.Parameters.Add(requesterParameter);

        return Assert.IsType<long>(await command.ExecuteScalarAsync());
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
