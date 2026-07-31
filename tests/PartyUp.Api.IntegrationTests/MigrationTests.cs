using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category","Integration")]
public sealed class MigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_CreatesEverySchemaObject()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        string[] relations =
        [
            "users",
            "platform_links",
            "username_registry",
            "ux_users_friend_code",
            "ix_users_grace_ends_at",
            "ix_users_lifecycle",
            "ix_users_banned_at",
            "ux_platform_links_user_id_platform",
            "ux_username_registry_owner_id_active",
        ];

        foreach (string relation in relations)
        {
            await AssertRelationExistsAsync(context, relation);
        }

        await AssertConstraintExistsAsync(context, "fk_platform_links_users_user_id");
        await AssertConstraintExistsAsync(context, "fk_username_registry_users_owner_id");
        await AssertConstraintExistsAsync(context, "fk_users_platform_links_avatar_source_platform_link_id");
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
