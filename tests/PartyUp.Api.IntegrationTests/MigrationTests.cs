using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure;
using PartyUp.Infrastructure.Identity;
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
            "auth_users",
            "auth_roles",
            "auth_user_roles",
            "auth_user_logins",
            "auth_user_claims",
            "auth_role_claims",
            "auth_user_tokens",
            "ux_auth_users_normalized_email",
        ];

        foreach (string relation in relations)
        {
            await AssertRelationExistsAsync(context, relation);
        }

        await AssertConstraintExistsAsync(context, "fk_platform_links_users_user_id");
        await AssertConstraintExistsAsync(context, "fk_username_registry_users_owner_id");
        await AssertConstraintExistsAsync(context, "fk_users_platform_links_avatar_source_platform_link_id");
        await AssertConstraintExistsAsync(context, "fk_auth_users_users_id");
    }

    [Fact]
    public async Task Migrate_RoundTripsAnIdentityLockoutEnd()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Db"] = _postgres.GetConnectionString(),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider();
        Instant expected = Instant.FromUtc(2026, 8, 1, 12, 30);
        UserId userId;

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Database.MigrateAsync();
            User user = User.BeginOnboarding(
                UserId.New(),
                FriendCode.Create("PU-LOCK99"),
                AgeGateSource.SelfAttested,
                SystemClock.Instance);
            userId = user.Id;
            context.DomainUsers.Add(user);
            await context.SaveChangesAsync();

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            string identityName = userId.Value.ToString("D");
            var authUser = new AuthUser
            {
                Id = userId.Value,
                UserName = identityName,
                NormalizedUserName = identityName,
                LockoutEnabled = true,
            };
            Assert.True((await userManager.CreateAsync(authUser)).Succeeded);
            Assert.True((await userManager.SetLockoutEndDateAsync(
                authUser,
                expected.ToDateTimeOffset())).Succeeded);
        }

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        var verificationManager = verificationScope.ServiceProvider
            .GetRequiredService<UserManager<AuthUser>>();
        AuthUser reloaded = (await verificationManager.FindByIdAsync(userId.Value.ToString("D")))!;
        Assert.Equal(expected, Instant.FromDateTimeOffset(reloaded.LockoutEnd!.Value));
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
