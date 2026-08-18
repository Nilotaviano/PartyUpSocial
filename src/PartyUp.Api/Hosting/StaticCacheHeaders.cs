namespace PartyUp.Api.Hosting;

/// <summary>
/// The <c>Cache-Control</c> policy for the SPA's static files (D-218/D-219). Build-hashed bundles
/// under <c>/assets/</c> are content-addressed and may be cached forever; everything else — the
/// document, the service worker, the manifest and the brand marks — is served <c>no-cache</c> so a
/// deploy takes effect on the next visit rather than after a cache expiry the user cannot clear.
/// </summary>
public static class StaticCacheHeaders
{
    /// <summary>One year, immutable: only ever correct for a content-hashed filename.</summary>
    public const string Immutable = "public,max-age=31536000,immutable";

    /// <summary>Revalidate every time; the response may still be reused after a 304.</summary>
    public const string NoCache = "no-cache";

    /// <summary>
    /// Chooses the header for a static file served at <paramref name="requestPath"/> (the path the
    /// browser asked for, e.g. <c>/assets/index-a1b2c3.js</c> or <c>/sw.js</c>).
    /// </summary>
    public static string ForRequestPath(string? requestPath) =>
        requestPath is not null
        && requestPath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
            ? Immutable
            : NoCache;
}
