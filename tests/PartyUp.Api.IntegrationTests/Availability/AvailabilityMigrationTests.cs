using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §4.1/§4.2, S2's schema gate: the migration creates every availability table,
/// check, FK and index, and the shapes that must fail only at the database boundary do.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private static readonly Instant Base = Instant.FromUtc(2026, 9, 1, 0, 0, 0);

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task CreatesSeriesBlockAndSkipConstraints_D132()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        string[] relations =
        [
            "availability_series",
            "availability_blocks",
            "availability_occurrence_skips",
            "ix_availability_series_owner_effective",
            "ix_availability_blocks_owner_start_end",
            "ix_availability_blocks_source_series_occurrence",
        ];
        foreach (string relation in relations)
        {
            await AssertRelationExistsAsync(context, relation);
        }

        await AssertConstraintExistsAsync(context, "ck_availability_series_effective_range");
        await AssertConstraintExistsAsync(context, "ck_availability_series_grid");
        await AssertConstraintExistsAsync(context, "ck_availability_series_duration");
        await AssertConstraintExistsAsync(context, "ck_availability_blocks_range");
        await AssertConstraintExistsAsync(context, "ck_availability_blocks_grid");
        await AssertConstraintExistsAsync(context, "ck_availability_blocks_provenance");
        await AssertConstraintExistsAsync(context, "fk_availability_series_users_owner_id");
        await AssertConstraintExistsAsync(context, "fk_availability_blocks_users_owner_id");
        await AssertConstraintExistsAsync(context, "fk_availability_blocks_availability_series_source_series_id");
        await AssertConstraintExistsAsync(context, "fk_availability_occurrence_skips_availability_series_series_id");

        // No availability table carries an xmin concurrency token (§4.1).
        Assert.DoesNotContain(
            context.Model.FindEntityType(typeof(PartyUp.Domain.Availability.AvailabilitySeries))!.GetProperties(),
            property => property.IsConcurrencyToken);
        Assert.DoesNotContain(
            context.Model.FindEntityType(typeof(PartyUp.Domain.Availability.AvailabilityBlock))!.GetProperties(),
            property => property.IsConcurrencyToken);

        UserId ownerId = await SeedUserAsync(context);
        Guid seriesId = Guid.CreateVersion7();
        await InsertSeriesAsync(context, seriesId, ownerId, effectiveUntil: null);

        Guid materializedBlockId = Guid.CreateVersion7();
        await ExecuteAsync(context, $"""
            INSERT INTO availability_blocks
                (id, owner_id, start_time, end_time, source_series_id, source_occurrence_date)
            VALUES
                ({materializedBlockId}, {ownerId.Value}, {Base}, {Base + Duration.FromMinutes(30)},
                 {seriesId}, {new LocalDate(2026, 9, 7)})
            """);
        await ExecuteAsync(context, $"""
            INSERT INTO availability_occurrence_skips (series_id, occurrence_date)
            VALUES ({seriesId}, {new LocalDate(2026, 9, 14)})
            """);

        // ON DELETE RESTRICT: a users row backing a series cannot be hard-deleted (§4.1).
        PostgresException restrictException = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(context, $"DELETE FROM users WHERE id = {ownerId.Value}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, restrictException.SqlState);

        // ON DELETE CASCADE: deleting the series removes its materialized block and its skip (§12-21).
        await ExecuteAsync(context, $"DELETE FROM availability_series WHERE id = {seriesId}");
        Assert.Equal(0, await CountAsync(context, "availability_blocks", "id", materializedBlockId));
        Assert.Equal(0, await CountWhereAsync(context, "availability_occurrence_skips", "series_id", seriesId));
    }

    [Fact]
    public async Task OffGridInstantsAndDurationsAreRejectedAtTheDatabase_D071()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId ownerId = await SeedUserAsync(context);

        // Off-grid block start (07 minutes past the hour).
        PostgresException offGridBlock = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO availability_blocks (id, owner_id, start_time, end_time)
            VALUES ({Guid.CreateVersion7()}, {ownerId.Value}, {Base + Duration.FromMinutes(7)}, {Base + Duration.FromMinutes(37)})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, offGridBlock.SqlState);
        Assert.Equal("ck_availability_blocks_grid", offGridBlock.ConstraintName);

        // Duration not a multiple of 15.
        PostgresException badDuration = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO availability_series
                (id, owner_id, day_of_week, start_time, duration_minutes, zone_id, effective_from)
            VALUES
                ({Guid.CreateVersion7()}, {ownerId.Value}, 1, {new LocalTime(9, 0)}, 20, 'UTC', {new LocalDate(2026, 9, 1)})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, badDuration.SqlState);
        Assert.Equal("ck_availability_series_duration", badDuration.ConstraintName);

        // Duration outside 15..1440.
        PostgresException outOfRangeDuration = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO availability_series
                (id, owner_id, day_of_week, start_time, duration_minutes, zone_id, effective_from)
            VALUES
                ({Guid.CreateVersion7()}, {ownerId.Value}, 1, {new LocalTime(9, 0)}, 1455, 'UTC', {new LocalDate(2026, 9, 1)})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, outOfRangeDuration.SqlState);
        Assert.Equal("ck_availability_series_duration", outOfRangeDuration.ConstraintName);

        // Series start_time off the 15-minute grid.
        PostgresException offGridSeries = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO availability_series
                (id, owner_id, day_of_week, start_time, duration_minutes, zone_id, effective_from)
            VALUES
                ({Guid.CreateVersion7()}, {ownerId.Value}, 1, {new LocalTime(9, 5)}, 60, 'UTC', {new LocalDate(2026, 9, 1)})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, offGridSeries.SqlState);
        Assert.Equal("ck_availability_series_grid", offGridSeries.ConstraintName);
    }

    [Fact]
    public async Task EmptySeriesRangeIsLegalAndInvertedIsRejected_D143()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId ownerId = await SeedUserAsync(context);

        // Legal: effective_until == effective_from is the post-split IsEmpty state.
        Guid emptySeriesId = Guid.CreateVersion7();
        await InsertSeriesAsync(context, emptySeriesId, ownerId, effectiveUntil: new LocalDate(2026, 9, 1));
        Assert.Equal(1, await CountAsync(context, "availability_series", "id", emptySeriesId));

        // Illegal: effective_until < effective_from.
        PostgresException inverted = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO availability_series
                (id, owner_id, day_of_week, start_time, duration_minutes, zone_id, effective_from, effective_until)
            VALUES
                ({Guid.CreateVersion7()}, {ownerId.Value}, 1, {new LocalTime(9, 0)}, 60, 'UTC',
                 {new LocalDate(2026, 9, 5)}, {new LocalDate(2026, 9, 1)})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, inverted.SqlState);
        Assert.Equal("ck_availability_series_effective_range", inverted.ConstraintName);
    }

    [Fact]
    public async Task DuplicateOccurrenceSkipIsRejected_D134()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId ownerId = await SeedUserAsync(context);
        Guid seriesId = Guid.CreateVersion7();
        await InsertSeriesAsync(context, seriesId, ownerId, effectiveUntil: null);

        LocalDate occurrenceDate = new(2026, 9, 7);
        await ExecuteAsync(context, $"""
            INSERT INTO availability_occurrence_skips (series_id, occurrence_date)
            VALUES ({seriesId}, {occurrenceDate})
            """);

        PostgresException duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(context, $"""
            INSERT INTO availability_occurrence_skips (series_id, occurrence_date)
            VALUES ({seriesId}, {occurrenceDate})
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("pk_availability_occurrence_skips", duplicate.ConstraintName);
    }

    private static async Task InsertSeriesAsync(
        AppDbContext context, Guid seriesId, UserId ownerId, LocalDate? effectiveUntil) =>
        await ExecuteAsync(context, $"""
            INSERT INTO availability_series
                (id, owner_id, day_of_week, start_time, duration_minutes, zone_id, effective_from, effective_until)
            VALUES
                ({seriesId}, {ownerId.Value}, 1, {new LocalTime(9, 0)}, 60, 'UTC', {new LocalDate(2026, 9, 1)}, {effectiveUntil})
            """);

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

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }

    private static async Task<long> CountAsync(AppDbContext context, string table, string idColumn, Guid id) =>
        await CountWhereAsync(context, table, idColumn, id);

    private static async Task<long> CountWhereAsync(AppDbContext context, string table, string column, Guid value)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = @value";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "value";
        parameter.Value = value;
        command.Parameters.Add(parameter);

        return (long)(await command.ExecuteScalarAsync())!;
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

    private static async Task EnsureOpenAsync(DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
    }
}
