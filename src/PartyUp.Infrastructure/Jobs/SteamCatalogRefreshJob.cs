using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using PartyUp.Application.Abstractions;

namespace PartyUp.Infrastructure.Jobs;

/// <summary>
/// STEAM_CATALOG_DESIGN.md §1: the 6-hour <c>IStoreService/GetAppList</c> refresh. <c>runStartedAt</c>
/// (not max(<c>last_modified</c>)) becomes the next run's <c>if_modified_since</c> watermark, so clock
/// skew between Steam and us can only re-fetch, never miss. A null watermark's full backfill is the
/// same paging loop, not a separate code path. Any page failure aborts the run without advancing the
/// watermark — idempotent, no Hangfire retry storm against Steam.
/// </summary>
public sealed partial class SteamCatalogRefreshJob(
    ISteamAppListClient steamAppList,
    ISteamCatalogRepository catalog,
    IClock clock,
    ILogger<SteamCatalogRefreshJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Instant runStartedAt = clock.GetCurrentInstant();
        SteamCatalogState state = await catalog.GetStateAsync(cancellationToken);
        long? ifModifiedSince = state.Watermark?.ToUnixTimeSeconds();

        long? lastAppId = null;
        bool haveMore = true;
        while (haveMore)
        {
            SteamAppListPage page;
            try
            {
                page = await steamAppList.GetAppListPageAsync(
                    new SteamAppListRequest(lastAppId, ifModifiedSince),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPageFailed(logger, exception.GetType().Name);
                await catalog.SetStateAsync(state.Watermark, runStartedAt, lastRunSucceeded: false, cancellationToken);
                return;
            }

            if (page.Apps.Count > 0)
            {
                IReadOnlyList<SteamCatalogAppUpsert> upserts = [.. page.Apps.Select(app =>
                    new SteamCatalogAppUpsert(
                        app.AppId,
                        app.Name,
                        app.LastModifiedUnixSeconds is { } seconds
                            ? Instant.FromUnixTimeSeconds(seconds)
                            : null))];
                await catalog.UpsertBatchAsync(upserts, cancellationToken);
            }

            haveMore = page.HaveMoreResults;
            lastAppId = page.LastAppId;
        }

        await catalog.SetStateAsync(runStartedAt, runStartedAt, lastRunSucceeded: true, cancellationToken);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Steam catalog refresh page failed ({ExceptionType}); run aborted without advancing the watermark")]
    private static partial void LogPageFailed(ILogger logger, string exceptionType);
}
