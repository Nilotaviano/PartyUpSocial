namespace PartyUp.Application.Abstractions;

/// <summary>
/// STEAM_CATALOG_DESIGN.md §1: the <c>IStoreService/GetAppList</c> v1 caller, one page per call. The
/// job pages with <see cref="SteamAppListRequest.LastAppId"/> until the response's
/// <see cref="SteamAppListPage.HaveMoreResults"/> is false.
/// </summary>
public interface ISteamAppListClient
{
    Task<SteamAppListPage> GetAppListPageAsync(
        SteamAppListRequest request, CancellationToken cancellationToken);
}

/// <param name="LastAppId">Paging cursor from the previous page's response; null on the first page.</param>
/// <param name="IfModifiedSinceUnixSeconds">
/// Non-null only when <c>steam_catalog_state.watermark</c> is non-null (§1) — the null-watermark run
/// is the full backfill, with no separate code path.
/// </param>
public sealed record SteamAppListRequest(long? LastAppId, long? IfModifiedSinceUnixSeconds);

public sealed record SteamAppListPage(
    IReadOnlyList<SteamAppListItem> Apps,
    long? LastAppId,
    bool HaveMoreResults);

public sealed record SteamAppListItem(long AppId, string Name, long? LastModifiedUnixSeconds);
