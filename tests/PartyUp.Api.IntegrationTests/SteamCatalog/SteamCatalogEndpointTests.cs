using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Application.Abstractions;
using PartyUp.Infrastructure.Jobs;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.SteamCatalog;

namespace PartyUp.Api.IntegrationTests.SteamCatalog;

/// <summary>
/// STEAM_CATALOG_DESIGN.md §3/§6: execution coverage for the trigram search (F2) — the endpoint's
/// similarity ranking, the 2-char floor, the empty-catalog <c>catalogAvailable: false</c> flag, and
/// the RequireOnboarded auth gate. Boots a real Postgres via <see cref="PartyUpApiFactory"/> (same
/// convention as <c>Avatars/AvatarProxyEndpointTests</c>), so <c>CREATE EXTENSION pg_trgm</c> from the
/// migration and <c>FromSqlInterpolated</c>/<c>unnest</c> both actually execute.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SteamCatalogEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ReturnsTheObviousMatchFirst_SimilarityRankingWorks_D194()
    {
        await SeedAppsAsync(
            (730, "Counter-Strike 2"),
            (413150, "Stardew Valley"),
            (1174180, "Red Dead Redemption 2"));
        Domain.Kernel.UserId userId = await _factory.SeedUserAsync(
            email: $"steam-catalog-rank-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, userId);

        HttpResponseMessage response = await session.GetAsync("/api/v1/steam-catalog?query=counter strike");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("catalogAvailable").GetBoolean());
        JsonElement results = root.GetProperty("results");
        Assert.True(results.GetArrayLength() >= 1);
        Assert.Equal("Counter-Strike 2", results[0].GetProperty("name").GetString());
        Assert.Equal(730, results[0].GetProperty("appId").GetInt64());
    }

    [Fact]
    public async Task QueryShorterThanTwoChars_Returns200WithEmptyResults_D194()
    {
        await SeedAppsAsync((730, "Counter-Strike 2"));
        Domain.Kernel.UserId userId = await _factory.SeedUserAsync(
            email: $"steam-catalog-short-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, userId);

        HttpResponseMessage response = await session.GetAsync("/api/v1/steam-catalog?query=c");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("catalogAvailable").GetBoolean());
        Assert.Equal(0, root.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task EmptyCatalog_ReportsCatalogAvailableFalse_D194()
    {
        Domain.Kernel.UserId userId = await _factory.SeedUserAsync(
            email: $"steam-catalog-empty-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, userId);

        HttpResponseMessage response = await session.GetAsync("/api/v1/steam-catalog?query=counter");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("catalogAvailable").GetBoolean());
        Assert.Equal(0, root.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task Unauthenticated_Returns401_D194()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/steam-catalog?query=counter");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpsertBatchAsync_InsertsThenUpdatesOnSecondCall_ChangedNameWins_D194()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISteamCatalogRepository>();

        await repository.UpsertBatchAsync(
            [new SteamCatalogAppUpsert(730, "Counter-Strike 2 Beta", null)], default);
        IReadOnlyList<SteamCatalogSearchResult> firstPass =
            await repository.SearchAsync("counter-strike 2 beta", 5, default);
        Assert.Contains(firstPass, r => r.AppId == 730 && r.Name == "Counter-Strike 2 Beta");

        await repository.UpsertBatchAsync(
            [new SteamCatalogAppUpsert(730, "Counter-Strike 2", null)], default);
        IReadOnlyList<SteamCatalogSearchResult> secondPass =
            await repository.SearchAsync("counter-strike 2", 5, default);
        Assert.Contains(secondPass, r => r.AppId == 730 && r.Name == "Counter-Strike 2");
        Assert.DoesNotContain(secondPass, r => r.Name == "Counter-Strike 2 Beta");
    }

    /// <summary>
    /// F2: a single unnest/ON CONFLICT statement historically threw "ON CONFLICT DO UPDATE command
    /// cannot affect row a second time" when a page repeated an app_id. UpsertBatchAsync dedupes
    /// last-wins before the statement now; this proves the same app_id twice in one batch no longer
    /// throws and the later entry wins.
    /// </summary>
    [Fact]
    public async Task UpsertBatchAsync_SameAppIdTwiceInOneBatch_DoesNotThrow_LastWins_D194()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISteamCatalogRepository>();

        await repository.UpsertBatchAsync(
            [
                new SteamCatalogAppUpsert(1091500, "Cyberpunk (stale)", null),
                new SteamCatalogAppUpsert(1091500, "Cyberpunk 2077", null),
            ],
            default);

        IReadOnlyList<SteamCatalogSearchResult> results =
            await repository.SearchAsync("cyberpunk 2077", 5, default);
        Assert.Contains(results, r => r.AppId == 1091500 && r.Name == "Cyberpunk 2077");
    }

    [Fact]
    public async Task RefreshJob_BackfillThenIncrementalRun_UpdatesWatermark_D194()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISteamCatalogRepository>();

        Instant firstRunAt = Instant.FromUtc(2026, 8, 12, 0, 0, 0);
        var backfillClient = new FakeSteamAppListClient(
        [
            new SteamAppListPage([new SteamAppListItem(730, "Counter-Strike 2", null)], LastAppId: 730, HaveMoreResults: false),
        ]);
        var job = new SteamCatalogRefreshJob(
            backfillClient, repository, new FixedClock(firstRunAt), NullLogger<SteamCatalogRefreshJob>.Instance);
        await job.ExecuteAsync(default);

        SteamCatalogState afterBackfill = await repository.GetStateAsync(default);
        Assert.Equal(firstRunAt, afterBackfill.Watermark);
        Assert.True(afterBackfill.LastRunSucceeded);

        Instant secondRunAt = firstRunAt + Duration.FromHours(6);
        var incrementalClient = new FakeSteamAppListClient(
        [
            new SteamAppListPage([new SteamAppListItem(1091500, "Cyberpunk 2077", null)], LastAppId: 1091500, HaveMoreResults: false),
        ]);
        var incrementalJob = new SteamCatalogRefreshJob(
            incrementalClient, repository, new FixedClock(secondRunAt), NullLogger<SteamCatalogRefreshJob>.Instance);
        await incrementalJob.ExecuteAsync(default);

        SteamCatalogState afterIncremental = await repository.GetStateAsync(default);
        Assert.Equal(secondRunAt, afterIncremental.Watermark);
        Assert.True(afterIncremental.LastRunSucceeded);
        Assert.Equal(firstRunAt.ToUnixTimeSeconds(), incrementalClient.Requests[0].IfModifiedSinceUnixSeconds);
    }

    private async Task SeedAppsAsync(params (long AppId, string Name)[] apps)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach ((long appId, string name) in apps)
        {
            context.SteamApps.Add(new SteamCatalogApp(appId, name, null));
        }

        await context.SaveChangesAsync();
    }

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private sealed class FakeSteamAppListClient(IReadOnlyList<SteamAppListPage> pages) : ISteamAppListClient
    {
        public List<SteamAppListRequest> Requests { get; } = [];

        public Task<SteamAppListPage> GetAppListPageAsync(
            SteamAppListRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(pages[Requests.Count - 1]);
        }
    }
}
