using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Hosting;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AdminSeederTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Seed_AssignsTheConfiguredAdminRole_D177()
    {
        const string email = "configured-admin@example.test";
        UserId userId = await _factory.SeedUserAsync(email: email);
        await using WebApplicationFactory<Program> configured = ConfigureSeeds(
            seedEmails: [email]);

        await RunSeederAsync(configured);

        Assert.True(await HasAdminRoleAsync(userId));
    }

    [Fact]
    public async Task Seed_RefusesAnUnconfirmedEmailMatch_D177()
    {
        const string email = "unconfirmed-admin@example.test";
        UserId userId = await _factory.SeedUserAsync(
            email: email,
            emailConfirmed: false);
        var sink = new CapturingSink();
        await using WebApplicationFactory<Program> configured = ConfigureSeeds(
            seedEmails: [email],
            sink: sink);

        AdminSeeder seeder = await RunSeederAsync(configured);

        Assert.False(await HasAdminRoleAsync(userId));
        Assert.Contains(sink.Events, logEvent =>
            logEvent.Level == LogEventLevel.Error &&
            logEvent.RenderMessage(CultureInfo.InvariantCulture).Contains(
                "unconfirmed account",
                StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Events, logEvent =>
            logEvent.RenderMessage(CultureInfo.InvariantCulture).Contains(
                email,
                StringComparison.OrdinalIgnoreCase));

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthUser account = await context.AuthUsers.SingleAsync(user => user.Id == userId.Value);
            account.EmailConfirmed = true;
            await context.SaveChangesAsync();
        }
        await seeder.SeedAsync(default);
        Assert.False(await HasAdminRoleAsync(userId));
    }

    [Fact]
    public async Task Seed_PrefersSeedUserIds_D177()
    {
        UserId idSeeded = await _factory.SeedUserAsync(
            email: "id-admin@example.test");
        UserId emailSeeded = await _factory.SeedUserAsync(
            email: "fallback-admin@example.test");
        await using WebApplicationFactory<Program> configured = ConfigureSeeds(
            seedUserIds: [idSeeded.Value.ToString("D")],
            seedEmails: ["fallback-admin@example.test"]);

        await RunSeederAsync(configured);

        Assert.True(await HasAdminRoleAsync(idSeeded));
        Assert.False(await HasAdminRoleAsync(emailSeeded));
    }

    [Fact]
    public async Task Seed_RotatesTheStampSoTheRoleTakesEffect_D177()
    {
        UserId userId = await _factory.SeedUserAsync();
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, userId);
        string originalStamp = await SecurityStampAsync(userId);
        await using WebApplicationFactory<Program> configured = ConfigureSeeds(
            seedUserIds: [userId.Value.ToString("D")]);

        AdminSeeder seeder = await RunSeederAsync(configured);
        string grantedStamp = await SecurityStampAsync(userId);

        Assert.NotEqual(originalStamp, grantedStamp);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/_slice2/authenticated");
        request.Headers.Add(HeaderNames.Cookie, cookie);
        HttpResponseMessage rejected = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        await seeder.SeedAsync(default);
        Assert.Equal(grantedStamp, await SecurityStampAsync(userId));
    }

    [Fact]
    public async Task Seed_CreatesTheAdminRoleRowWhenAbsent_D177()
    {
        UserId userId = await _factory.SeedUserAsync();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthRole role = await context.Roles.SingleAsync(role => role.NormalizedName == "ADMIN");
            context.Roles.Remove(role);
            await context.SaveChangesAsync();
        }
        await using WebApplicationFactory<Program> configured = ConfigureSeeds(
            seedUserIds: [userId.Value.ToString("D")]);

        await RunSeederAsync(configured);

        await using AsyncServiceScope assertionScope = _factory.Services.CreateAsyncScope();
        AppDbContext assertionContext =
            assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthRole createdRole = await assertionContext.Roles
            .SingleAsync(role => role.NormalizedName == "ADMIN");
        IdentityUserRole<Guid> grant = await assertionContext.Set<IdentityUserRole<Guid>>()
            .SingleAsync(userRole => userRole.UserId == userId.Value);
        Assert.Equal(createdRole.Id, grant.RoleId);
    }

    private WebApplicationFactory<Program> ConfigureSeeds(
        IReadOnlyList<string>? seedUserIds = null,
        IReadOnlyList<string>? seedEmails = null,
        CapturingSink? sink = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>();
                AddValues(values, "Admin:SeedUserIds", seedUserIds);
                AddValues(values, "Admin:SeedEmails", seedEmails);
                configuration.AddInMemoryCollection(values);
            });
            if (sink is not null)
            {
                builder.ConfigureServices(services =>
                    services.AddSerilog((provider, logger) => logger
                        .ReadFrom.Configuration(provider.GetRequiredService<IConfiguration>())
                        .WriteTo.Sink(sink)));
            }
        });

    private static void AddValues(
        Dictionary<string, string?> values,
        string key,
        IReadOnlyList<string>? configured)
    {
        for (int index = 0; index < (configured?.Count ?? 0); index++)
        {
            values[$"{key}:{index}"] = configured![index];
        }
    }

    private static async Task<AdminSeeder> RunSeederAsync(
        WebApplicationFactory<Program> factory)
    {
        AdminSeeder seeder = factory.Services.GetRequiredService<AdminSeeder>();
        await seeder.SeedAsync(default);
        return seeder;
    }

    private async Task<bool> HasAdminRoleAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid? adminRoleId = await context.Roles
            .Where(role => role.NormalizedName == "ADMIN")
            .Select(role => (Guid?)role.Id)
            .SingleOrDefaultAsync();
        return adminRoleId is { } roleId && await context.Set<IdentityUserRole<Guid>>()
            .AnyAsync(userRole =>
                userRole.UserId == userId.Value && userRole.RoleId == roleId);
    }

    private async Task<string> SecurityStampAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await context.AuthUsers
            .AsNoTracking()
            .SingleAsync(user => user.Id == userId.Value)).SecurityStamp!;
    }

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        internal IReadOnlyCollection<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
