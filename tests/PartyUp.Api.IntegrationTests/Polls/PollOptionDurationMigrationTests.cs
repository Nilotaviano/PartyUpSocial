using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// D-211's data migration: every option that existed under the poll-level column keeps the length it
/// effectively had. Proven the AvatarCatalogBackfillMigrationTests way — migrate to the pre-D-211
/// head, seed rows the old way through raw SQL (the current model has no polls.duration_minutes to
/// write through), migrate forward, and read the option column back.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollOptionDurationMigrationTests : IAsyncLifetime
{
    private const string PrePerOptionHead = "20260816212909_AddFriendGroupsAndContentVisibility";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_BackfillsEveryOptionFromItsPollsOldDuration_D211()
    {
        await using AppDbContext context = CreateContext();
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PrePerOptionHead);

        UserId organizerId = await SeedUserAsync(context);
        Instant deadline = Instant.FromUtc(2026, 9, 2, 12, 0);

        Guid shortPollId = await SeedPollRawAsync(context, organizerId, deadline, durationMinutes: 45);
        Guid shortOptionA = await SeedOptionRawAsync(context, shortPollId, Instant.FromUtc(2026, 9, 5, 18, 0));
        Guid shortOptionB = await SeedOptionRawAsync(context, shortPollId, Instant.FromUtc(2026, 9, 6, 18, 0));

        Guid longPollId = await SeedPollRawAsync(context, organizerId, deadline, durationMinutes: 1440);
        Guid longOption = await SeedOptionRawAsync(context, longPollId, Instant.FromUtc(2026, 9, 7, 18, 0));

        // A poll with no options at all must not block the NOT NULL tightening.
        await SeedPollRawAsync(context, organizerId, deadline, durationMinutes: 120);

        await migrator.MigrateAsync();

        Assert.Equal(45, await ReadOptionDurationAsync(context, shortOptionA));
        Assert.Equal(45, await ReadOptionDurationAsync(context, shortOptionB));
        Assert.Equal(1440, await ReadOptionDurationAsync(context, longOption));

        // The poll-level column and its check are gone: the option is the only place a length lives.
        Assert.False(await ColumnExistsAsync(context, "polls", "duration_minutes"));
        Assert.False(await ConstraintExistsAsync(context, "ck_polls_duration"));
        Assert.True(await ConstraintExistsAsync(context, "ck_poll_options_duration"));

        // D-142 per option, enforced at the boundary the backfill just created.
        PostgresException violation = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(context, $"""
                UPDATE poll_options SET duration_minutes = 50 WHERE id = {longOption}
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, violation.SqlState);
        Assert.Equal("ck_poll_options_duration", violation.ConstraintName);
    }

    private static async Task<Guid> SeedPollRawAsync(
        AppDbContext context,
        UserId organizerId,
        Instant deadline,
        int durationMinutes)
    {
        Guid pollId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO polls
                (id, organizer_id, title, duration_minutes, platform, notes, min_attendance,
                 max_capacity, visibility, deadline_choice, deadline, status, created_at, selection_window_end)
            VALUES
                ({pollId}, {organizerId.Value}, 'Pre-D211 poll', {durationMinutes}, 3, '', 1, NULL, 1, 2,
                 {deadline}, 0, {deadline - Duration.FromHours(24)}, {deadline + Duration.FromHours(48)})
            """);
        return pollId;
    }

    private static async Task<Guid> SeedOptionRawAsync(AppDbContext context, Guid pollId, Instant startTime)
    {
        Guid optionId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO poll_options (id, poll_id, start_time, created_at)
            VALUES ({optionId}, {pollId}, {startTime}, now())
            """);
        return optionId;
    }

    /// <summary>
    /// The users mapping is untouched by this migration, so the domain object writes cleanly at the
    /// pre-D-211 head; only the poll rows below need raw SQL.
    /// </summary>
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

    private static async Task<int> ReadOptionDurationAsync(AppDbContext context, Guid optionId) =>
        (int)(await ReadScalarAsync(
            context,
            $"SELECT duration_minutes FROM poll_options WHERE id = '{optionId}'"))!;

    private static async Task<bool> ColumnExistsAsync(AppDbContext context, string table, string column) =>
        await ReadScalarAsync(
            context,
            $"""
            SELECT 1 FROM information_schema.columns
            WHERE table_name = '{table}' AND column_name = '{column}'
            """) is not null;

    private static async Task<bool> ConstraintExistsAsync(AppDbContext context, string constraint) =>
        await ReadScalarAsync(
            context,
            $"SELECT 1 FROM pg_constraint WHERE conname = '{constraint}'") is not null;

    private static async Task<object?> ReadScalarAsync(AppDbContext context, string commandText)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options);
}
