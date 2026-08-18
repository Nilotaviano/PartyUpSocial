using PartyUp.Api.Hosting;

namespace PartyUp.Api.IntegrationTests.Hosting;

/// <summary>
/// D-218's cache policy for the statically served SPA: only the build-hashed bundles may be
/// cached immutably, and the two files an update has to travel through — the service worker and
/// the manifest — must never be.
/// </summary>
public sealed class StaticCacheHeadersTests
{
    [Fact]
    public void BuildHashedBundles_AreCachedImmutably_D218()
    {
        Assert.Equal("public,max-age=31536000,immutable", StaticCacheHeaders.ForRequestPath("/assets/xyz.js"));
        Assert.Equal("public,max-age=31536000,immutable", StaticCacheHeaders.ForRequestPath("/assets/index-a1b2c3.css"));
    }

    [Theory]
    [InlineData("/sw.js")]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/index.html")]
    [InlineData("/icon-192.png")]
    [InlineData("/icon-maskable-512.png")]
    [InlineData("/favicon.svg")]
    [InlineData("/")]
    public void EverythingOutsideTheHashedBundles_Revalidates_D218(string requestPath)
    {
        Assert.Equal("no-cache", StaticCacheHeaders.ForRequestPath(requestPath));
    }

    [Fact]
    public void NoPathAtAll_Revalidates_D218()
    {
        Assert.Equal("no-cache", StaticCacheHeaders.ForRequestPath(null));
    }

    /// <summary>
    /// The regression this class exists for: a worker or a manifest served immutably strands every
    /// visitor on the deployed-at version until they clear their cache, since the worker is the
    /// very thing that would fetch the update.
    /// </summary>
    [Theory]
    [InlineData("/sw.js")]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/nested/assets/sw.js")]
    [InlineData("/swassets/sw.js")]
    public void TheServiceWorkerAndManifest_CanNeverBeImmutable_D218(string requestPath)
    {
        Assert.NotEqual(StaticCacheHeaders.Immutable, StaticCacheHeaders.ForRequestPath(requestPath));
    }
}
