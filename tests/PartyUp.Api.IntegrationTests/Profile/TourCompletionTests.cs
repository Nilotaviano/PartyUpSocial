using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Profile;

/// <summary>
/// TOUR_DESIGN.md §1/§4 (D-182): <c>POST /api/v1/me/tour-completed</c> idempotency, the "onboarded
/// accounts only" migration backfill in both directions, and the guest-surface absence of any tour
/// route.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TourCompletionTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task CompleteTour_IsIdempotent_FirstInstantWins_D182()
    {
        SessionHttpUser user = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, user.Id);

        using JsonDocument before = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync("/api/v1/me"));
        Assert.False(before.RootElement.GetProperty("tourCompleted").GetBoolean());

        HttpResponseMessage first = await session.SendUnsafeAsync(HttpMethod.Post, "/api/v1/me/tour-completed");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        Instant? firstInstant = await ReadTourCompletedAtAsync(user.Id);
        Assert.True(firstInstant.HasValue);

        HttpResponseMessage second = await session.SendUnsafeAsync(HttpMethod.Post, "/api/v1/me/tour-completed");
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        Instant? secondInstant = await ReadTourCompletedAtAsync(user.Id);
        Assert.Equal(firstInstant, secondInstant);

        using JsonDocument after = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync("/api/v1/me"));
        Assert.True(after.RootElement.GetProperty("tourCompleted").GetBoolean());
    }

    [Fact]
    public async Task CompleteTour_RequiresOnboarding_NotJustAuthentication_D182()
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create($"PU-{Guid.NewGuid():N}"[..9]),
            AgeGateSource.SelfAttested,
            _factory.Clock);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.DomainUsers.Add(user);
            await context.SaveChangesAsync();

            var userManager = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AuthUser>>();
            string identityName = user.Id.Value.ToString("D");
            Microsoft.AspNetCore.Identity.IdentityResult identity = await userManager.CreateAsync(
                new AuthUser
                {
                    Id = user.Id.Value,
                    UserName = identityName,
                    NormalizedUserName = identityName,
                    Email = $"tour-onboarding-{Guid.NewGuid():N}@example.test",
                    EmailConfirmed = true,
                    LockoutEnabled = true,
                });
            Assert.True(identity.Succeeded);
        }

        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, user.Id);
        HttpResponseMessage response = await session.SendUnsafeAsync(HttpMethod.Post, "/api/v1/me/tour-completed");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void GuestSurface_HasNoTourRoute_D182()
    {
        IEnumerable<Microsoft.AspNetCore.Routing.EndpointDataSource> dataSources = _factory.Services
            .GetRequiredService<IEnumerable<Microsoft.AspNetCore.Routing.EndpointDataSource>>();
        IEnumerable<string> guestRoutes = dataSources
            .SelectMany(source => source.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/api/v1/guest/",
                StringComparison.Ordinal) == true)
            .Select(endpoint => endpoint.RoutePattern.RawText!);

        Assert.DoesNotContain(
            guestRoutes,
            route => route.Contains("tour-completed", StringComparison.Ordinal));
    }

    private async Task<Instant?> ReadTourCompletedAtAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.DomainUsers
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.TourCompletedAt)
            .SingleAsync();
    }
}

/// <summary>
/// TOUR_DESIGN.md §1: the onboarded-only backfill predicate, proven both directions by migrating to
/// the pre-tour head, seeding rows the old way (mirroring
/// <c>PushTransportMigrationTests.Migrate_BackfillsEveryPreExistingNotificationToSkipped_D112</c>),
/// then migrating forward and reading back.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TourCompletedAtBackfillMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_BackfillsOnboardedRows_AndLeavesMidOnboardingRowsNull_D182()
    {
        await using AppDbContext context = CreateContext();
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260809142933_AddPushTransportPersistence");

        Guid onboardedActiveId = await SeedRawUserAsync(
            context,
            lifecycle: 1,
            username: "onboarded-active",
            displayName: "Onboarded Active",
            timeZoneId: "America/Fortaleza",
            locale: "en",
            avatarKind: 0,
            avatarEmoji: "🙂");

        Guid onboardedPendingDeletionId = await SeedRawUserAsync(
            context,
            lifecycle: 2,
            username: "onboarded-pending-deletion",
            displayName: "Onboarded Pending Deletion",
            timeZoneId: "America/Fortaleza",
            locale: "en",
            avatarKind: 1,
            avatarEmoji: null,
            avatarSourcePlatformLinkId: null);

        Guid midOnboardingId = await SeedRawUserAsync(
            context,
            lifecycle: 0,
            username: null,
            displayName: null,
            timeZoneId: null,
            locale: null,
            avatarKind: null,
            avatarEmoji: null);

        // A PendingDeletion row reached straight from Onboarding (RequestDeletion allows both
        // starting lifecycles) with a still-incomplete profile -- lifecycle alone is not "onboarded".
        Guid pendingDeletionIncompleteProfileId = await SeedRawUserAsync(
            context,
            lifecycle: 2,
            username: null,
            displayName: null,
            timeZoneId: null,
            locale: null,
            avatarKind: null,
            avatarEmoji: null);

        await migrator.MigrateAsync();

        Instant? active = await ReadTourCompletedAtAsync(context, onboardedActiveId);
        Instant? pendingDeletionComplete = await ReadTourCompletedAtAsync(context, onboardedPendingDeletionId);
        Instant? midOnboarding = await ReadTourCompletedAtAsync(context, midOnboardingId);
        Instant? pendingDeletionIncomplete =
            await ReadTourCompletedAtAsync(context, pendingDeletionIncompleteProfileId);

        Assert.True(active.HasValue, "onboarded Active row should be backfilled.");
        Assert.True(
            pendingDeletionComplete.HasValue,
            "onboarded PendingDeletion (complete profile) row should be backfilled.");
        Assert.False(midOnboarding.HasValue, "mid-onboarding row should stay null.");
        Assert.False(
            pendingDeletionIncomplete.HasValue,
            "PendingDeletion reached straight from Onboarding with an incomplete profile should stay null.");
    }

    /// <summary>
    /// Reads via a raw ADO command rather than <c>context.DomainUsers</c>: the row was seeded
    /// pre-migration against a column set EF's current model does not match, so a typed LINQ query
    /// risks EF materializing columns this schema snapshot doesn't have yet at that point in the
    /// test. With Npgsql's NodaTime plugin (<c>CreateContext</c>'s own <c>UseNodaTime()</c>), a
    /// <c>timestamptz</c> scalar comes back as a boxed <see cref="Instant"/>, never
    /// <see cref="DateTime"/>.
    /// </summary>
    private static async Task<Instant?> ReadTourCompletedAtAsync(AppDbContext context, Guid userId)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT tour_completed_at FROM users WHERE id = @id";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = userId;
        command.Parameters.Add(parameter);

        object? result = await command.ExecuteScalarAsync();
        return result as Instant?;
    }

    private static async Task<Guid> SeedRawUserAsync(
        AppDbContext context,
        int lifecycle,
        string? username,
        string? displayName,
        string? timeZoneId,
        string? locale,
        int? avatarKind,
        string? avatarEmoji,
        Guid? avatarSourcePlatformLinkId = null)
    {
        Guid id = Guid.CreateVersion7();
        string? friendCode = username is null ? null : $"PU{Guid.NewGuid():N}"[..9].ToUpperInvariant();

        // Inserted via raw SQL against the pre-tour column set (mirroring
        // FriendGraphMigrationTests/PushTransportMigrationTests' own backfill-test idiom): the
        // current AppDbContext model already carries tour_completed_at, so a domain-object
        // SaveChanges here would generate an INSERT against a column the DB at this migration point
        // does not have yet.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users
                (id, lifecycle, username, display_name, time_zone_id, locale, friend_code,
                 age_gate_passed, age_gate_source, reminder_default, created_at,
                 avatar_kind, avatar_emoji, avatar_source_platform_link_id)
            VALUES
                ({id}, {lifecycle}, {username}, {displayName}, {timeZoneId}, {locale}, {friendCode},
                 true, 0, 3, now(),
                 {avatarKind}, {avatarEmoji}, {avatarSourcePlatformLinkId})
            """);
        return id;
    }

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
