using Microsoft.EntityFrameworkCore;
using Npgsql;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests;

/// <summary>
/// The one Postgres container the integration suite runs against, plus the pre-migrated template
/// database every <see cref="PartyUpApiFactory"/> clones its own schema from.
/// <para>
/// Booting a container and replaying every EF migration once per test class dominated the suite's
/// wall time. Instead the container starts lazily on the first factory that needs it and the
/// migrations run exactly once, into <c>partyup_template</c>; each factory then issues
/// <c>CREATE DATABASE … TEMPLATE partyup_template</c>, which Postgres services as a file copy. The
/// resulting database is byte-identical to a freshly migrated one — including the EF migrations
/// history table — so <c>Database:MigrateOnStartup</c> stays on in the factory and every host still
/// exercises the production startup path, at the cost of one cheap history query.
/// </para>
/// <para>
/// xUnit v2 has no assembly-level fixture, so the lifetime is a process-wide static: the container
/// is never stopped here and Testcontainers' Ryuk reaper removes it when the test process exits.
/// </para>
/// </summary>
internal static class SharedPostgres
{
    private const string TemplateDatabase = "partyup_template";

    /// <summary>
    /// Postgres takes an exclusive lock on a template database for the duration of a clone and
    /// rejects a concurrent <c>CREATE DATABASE</c> from the same template outright, so the parallel
    /// test classes queue here rather than race.
    /// </summary>
    private static readonly SemaphoreSlim CloneGate = new(1, 1);

    private static readonly Lazy<Task<PostgreSqlContainer>> Container =
        new(StartContainerAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Provisions a fresh, fully migrated database on the shared container and returns the
    /// connection string that reaches it.
    /// </summary>
    internal static async Task<string> CreateDatabaseAsync()
    {
        PostgreSqlContainer container = await Container.Value;
        string database = "test_" + Guid.NewGuid().ToString("N");

        await CloneGate.WaitAsync();
        try
        {
            await ExecuteAsync(
                MaintenanceConnectionString(container),
                $"""CREATE DATABASE "{database}" TEMPLATE "{TemplateDatabase}" """);
        }
        finally
        {
            CloneGate.Release();
        }

        return ConnectionStringFor(container, database, pooling: true);
    }

    /// <summary>
    /// Best-effort reclaim of a database handed out by <see cref="CreateDatabaseAsync"/>. A failure
    /// here is never worth failing a test run over: the container itself is discarded when the
    /// process exits.
    /// </summary>
    internal static async Task DropDatabaseAsync(string connectionString)
    {
        PostgreSqlContainer container = await Container.Value;
        string database = new NpgsqlConnectionStringBuilder(connectionString).Database!;

        try
        {
            await ExecuteAsync(
                MaintenanceConnectionString(container),
                $"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        }
        catch (NpgsqlException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task<PostgreSqlContainer> StartContainerAsync()
    {
        PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
            // Durability buys nothing for a container that is thrown away at process exit, and
            // costs a fsync on every commit the suite makes. max_connections covers the pooled
            // connections of every test class running in parallel against this one server.
            .WithCommand(
                "-c", "fsync=off",
                "-c", "synchronous_commit=off",
                "-c", "full_page_writes=off",
                "-c", "max_connections=500")
            .Build();
        await container.StartAsync();

        await ExecuteAsync(
            MaintenanceConnectionString(container),
            $"""CREATE DATABASE "{TemplateDatabase}" """);

        // Pooling is off on the template's own connection string: CREATE DATABASE … TEMPLATE fails
        // while any session is still connected to the template, and a pooled connection outlives
        // the DbContext that opened it.
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                ConnectionStringFor(container, TemplateDatabase, pooling: false),
                npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var context = new AppDbContext(options))
        {
            await context.Database.MigrateAsync();
        }

        return container;
    }

    private static string MaintenanceConnectionString(PostgreSqlContainer container) =>
        ConnectionStringFor(container, "postgres", pooling: false);

    private static string ConnectionStringFor(
        PostgreSqlContainer container,
        string database,
        bool pooling) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = database,
            Pooling = pooling,
        }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
