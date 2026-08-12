using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Infrastructure.Jobs;

namespace PartyUp.Api.IntegrationTests.SteamCatalog;

/// <summary>
/// STEAM_CATALOG_DESIGN.md §1/§6: <see cref="SteamCatalogRefreshJob"/> against fakes for both Steam
/// and the repository -- no database, no Docker (mirrors
/// <c>RecurringJobRegistrationServiceRetryTests</c>'s untagged unit-test posture in this project).
/// </summary>
public sealed class SteamCatalogRefreshJobTests
{
    private static readonly Instant RunStartedAt = Instant.FromUtc(2026, 8, 12, 12, 0, 0);

    [Fact]
    public async Task NullWatermark_IsTheSameFullBackfillPagingLoop_D194()
    {
        FakeSteamAppListClient client = new(
        [
            new SteamAppListPage([new SteamAppListItem(1, "One", null)], LastAppId: 1, HaveMoreResults: true),
            new SteamAppListPage([new SteamAppListItem(2, "Two", null)], LastAppId: 2, HaveMoreResults: false),
        ]);
        FakeSteamCatalogRepository repository = new(new SteamCatalogState(null, null, false));
        SteamCatalogRefreshJob job = CreateJob(client, repository);

        await job.ExecuteAsync(default);

        Assert.Equal(2, client.Requests.Count);
        Assert.Null(client.Requests[0].LastAppId);
        Assert.Null(client.Requests[0].IfModifiedSinceUnixSeconds);
        Assert.Equal(1, client.Requests[1].LastAppId);
        Assert.Equal(2, repository.UpsertedBatches.Count);
        Assert.Equal(1, Assert.Single(repository.UpsertedBatches[0]).AppId);
        Assert.Equal(2, Assert.Single(repository.UpsertedBatches[1]).AppId);
    }

    [Fact]
    public async Task NonNullWatermark_PropagatesIfModifiedSinceOnEveryPage_D194()
    {
        Instant watermark = Instant.FromUtc(2026, 8, 1, 0, 0, 0);
        FakeSteamAppListClient client = new(
        [
            new SteamAppListPage([new SteamAppListItem(1, "One", null)], LastAppId: 1, HaveMoreResults: true),
            new SteamAppListPage([new SteamAppListItem(2, "Two", null)], LastAppId: 2, HaveMoreResults: false),
        ]);
        FakeSteamCatalogRepository repository = new(new SteamCatalogState(watermark, null, false));
        SteamCatalogRefreshJob job = CreateJob(client, repository);

        await job.ExecuteAsync(default);

        Assert.All(
            client.Requests,
            request => Assert.Equal(watermark.ToUnixTimeSeconds(), request.IfModifiedSinceUnixSeconds));
    }

    [Fact]
    public async Task FullSuccess_AdvancesTheWatermarkToRunStartedAt_D194()
    {
        FakeSteamAppListClient client = new(
        [
            new SteamAppListPage([new SteamAppListItem(1, "One", null)], LastAppId: 1, HaveMoreResults: false),
        ]);
        FakeSteamCatalogRepository repository = new(new SteamCatalogState(null, null, false));
        SteamCatalogRefreshJob job = CreateJob(client, repository);

        await job.ExecuteAsync(default);

        (Instant? watermark, Instant lastRunAt, bool lastRunSucceeded) = Assert.Single(repository.StateWrites);
        Assert.Equal(RunStartedAt, watermark);
        Assert.Equal(RunStartedAt, lastRunAt);
        Assert.True(lastRunSucceeded);
    }

    [Fact]
    public async Task APageFailure_AbortsWithoutAdvancingTheWatermark_D194()
    {
        Instant priorWatermark = Instant.FromUtc(2026, 8, 1, 0, 0, 0);
        FakeSteamAppListClient client = new(
        [
            new SteamAppListPage([new SteamAppListItem(1, "One", null)], LastAppId: 1, HaveMoreResults: true),
        ])
        {
            ThrowOnRequestIndex = 1,
        };
        FakeSteamCatalogRepository repository = new(new SteamCatalogState(priorWatermark, null, true));
        SteamCatalogRefreshJob job = CreateJob(client, repository);

        await job.ExecuteAsync(default);

        (Instant? watermark, Instant lastRunAt, bool lastRunSucceeded) = Assert.Single(repository.StateWrites);
        Assert.Equal(priorWatermark, watermark);
        Assert.Equal(RunStartedAt, lastRunAt);
        Assert.False(lastRunSucceeded);
        // The first page's upsert, already applied before the second page failed, is not rolled back
        // (rows are never deleted; a stale name is harmless -- §1).
        Assert.Single(repository.UpsertedBatches);
    }

    [Fact]
    public async Task HaveMoreResultsWithNoForwardProgressCursor_AbortsWithoutAdvancingTheWatermark_D194()
    {
        Instant priorWatermark = Instant.FromUtc(2026, 8, 1, 0, 0, 0);
        FakeSteamAppListClient client = new(
        [
            new SteamAppListPage([new SteamAppListItem(1, "One", null)], LastAppId: null, HaveMoreResults: true),
        ]);
        FakeSteamCatalogRepository repository = new(new SteamCatalogState(priorWatermark, null, true));
        SteamCatalogRefreshJob job = CreateJob(client, repository);

        await job.ExecuteAsync(default);

        // The loop must not spin forever re-requesting the same page.
        Assert.Single(client.Requests);
        (Instant? watermark, Instant lastRunAt, bool lastRunSucceeded) = Assert.Single(repository.StateWrites);
        Assert.Equal(priorWatermark, watermark);
        Assert.Equal(RunStartedAt, lastRunAt);
        Assert.False(lastRunSucceeded);
    }

    [Fact]
    public async Task HaveMoreResultsWithUnchangedLastAppId_AbortsWithoutAdvancingTheWatermark_D194()
    {
        Instant priorWatermark = Instant.FromUtc(2026, 8, 1, 0, 0, 0);
        FakeSteamAppListClient client = new(
        [
            new SteamAppListPage([new SteamAppListItem(1, "One", null)], LastAppId: 1, HaveMoreResults: true),
            new SteamAppListPage([new SteamAppListItem(1, "One", null)], LastAppId: 1, HaveMoreResults: true),
        ]);
        FakeSteamCatalogRepository repository = new(new SteamCatalogState(priorWatermark, null, true));
        SteamCatalogRefreshJob job = CreateJob(client, repository);

        await job.ExecuteAsync(default);

        Assert.Equal(2, client.Requests.Count);
        (Instant? watermark, Instant lastRunAt, bool lastRunSucceeded) = Assert.Single(repository.StateWrites);
        Assert.Equal(priorWatermark, watermark);
        Assert.Equal(RunStartedAt, lastRunAt);
        Assert.False(lastRunSucceeded);
    }

    [Fact]
    public async Task UpsertBatchShape_OnePageEqualsOneUpsertCall_D194()
    {
        FakeSteamAppListClient client = new(
        [
            new SteamAppListPage(
                [new SteamAppListItem(1, "One", 1_700_000_000), new SteamAppListItem(2, "Two", null)],
                LastAppId: 2,
                HaveMoreResults: false),
        ]);
        FakeSteamCatalogRepository repository = new(new SteamCatalogState(null, null, false));
        SteamCatalogRefreshJob job = CreateJob(client, repository);

        await job.ExecuteAsync(default);

        IReadOnlyList<SteamCatalogAppUpsert> batch = Assert.Single(repository.UpsertedBatches);
        Assert.Equal(2, batch.Count);
        Assert.Equal(Instant.FromUnixTimeSeconds(1_700_000_000), batch[0].LastModified);
        Assert.Null(batch[1].LastModified);
    }

    private static SteamCatalogRefreshJob CreateJob(
        ISteamAppListClient client, ISteamCatalogRepository repository) =>
        new(client, repository, new FixedClock(RunStartedAt), NullLogger<SteamCatalogRefreshJob>.Instance);

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private sealed class FakeSteamAppListClient(IReadOnlyList<SteamAppListPage> pages) : ISteamAppListClient
    {
        public List<SteamAppListRequest> Requests { get; } = [];
        public int? ThrowOnRequestIndex { get; init; }

        public Task<SteamAppListPage> GetAppListPageAsync(
            SteamAppListRequest request, CancellationToken cancellationToken)
        {
            int index = Requests.Count;
            Requests.Add(request);
            if (ThrowOnRequestIndex == index)
            {
                throw new HttpRequestException("Simulated Steam failure.");
            }

            return Task.FromResult(pages[index]);
        }
    }

    private sealed class FakeSteamCatalogRepository(SteamCatalogState initialState) : ISteamCatalogRepository
    {
        public List<IReadOnlyList<SteamCatalogAppUpsert>> UpsertedBatches { get; } = [];
        public List<(Instant? Watermark, Instant LastRunAt, bool LastRunSucceeded)> StateWrites { get; } = [];

        public Task<IReadOnlyList<SteamCatalogSearchResult>> SearchAsync(
            string term, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> HasAnyAppAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpsertBatchAsync(
            IReadOnlyList<SteamCatalogAppUpsert> apps, CancellationToken cancellationToken)
        {
            UpsertedBatches.Add(apps);
            return Task.CompletedTask;
        }

        public Task<SteamCatalogState> GetStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(initialState);

        public Task SetStateAsync(
            Instant? watermark, Instant lastRunAt, bool lastRunSucceeded, CancellationToken cancellationToken)
        {
            StateWrites.Add((watermark, lastRunAt, lastRunSucceeded));
            return Task.CompletedTask;
        }
    }
}
