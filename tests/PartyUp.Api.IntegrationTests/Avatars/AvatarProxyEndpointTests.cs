using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PartyUp.Api.Logging;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Avatars;

/// <summary>
/// AVATARS_DESIGN.md §3.3's proxy endpoint: a pure token -> bytes read outside <c>/api/v1</c>, no
/// outbound call and no database write on GET (A-064). §5's obligations: the happy path serves a
/// captured avatar; unknown token, deleted source and missing bytes all collapse to the same 404;
/// and the response headers are exactly as specified.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvatarProxyEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ServesACapturedAvatarByToken_D190()
    {
        UserId userId = await _factory.SeedUserAsync(email: $"avatar-proxy-{Guid.NewGuid():N}@example.test");
        byte[] bytes = [1, 2, 3, 4];
        string token = await CaptureAsync(userId, bytes, "image/webp");

        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using HttpResponseMessage response = await client.GetAsync($"/a/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ResponseHeadersMatchSpecExactly()
    {
        UserId userId = await _factory.SeedUserAsync(email: $"avatar-proxy-headers-{Guid.NewGuid():N}@example.test");
        string token = await CaptureAsync(userId, [1, 2, 3], "image/webp");

        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using HttpResponseMessage response = await client.GetAsync($"/a/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("default-src 'none'; sandbox", Single(response, "Content-Security-Policy"));
        Assert.Equal("inline", Single(response, "Content-Disposition"));
        Assert.Equal("private", Single(response, "Cache-Control"));
    }

    [Fact]
    public async Task UnknownToken_Returns404()
    {
        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using HttpResponseMessage response = await client.GetAsync("/a/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeletedSource_ReturnsTheSame404AsAnUnknownToken()
    {
        UserId userId = await _factory.SeedUserAsync(email: $"avatar-proxy-deleted-{Guid.NewGuid():N}@example.test");
        string token = await CaptureAsync(userId, [1, 2, 3], "image/webp");

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User user = await context.DomainUsers
                .Include(u => u.AvatarSources)
                .SingleAsync(u => u.Id == userId);
            AvatarSource source = user.AvatarSources.Single();
            user.RemoveAvatarSource(source.Id);
            await context.SaveChangesAsync();
        }

        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using HttpResponseMessage response = await client.GetAsync($"/a/{token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>AVATARS_DESIGN.md §5: "missing bytes" collapses to the same 404 as the other two cases.</summary>
    [Fact]
    public async Task MissingBytes_ReturnsTheSame404AsAnUnknownToken()
    {
        UserId userId = await _factory.SeedUserAsync(email: $"avatar-proxy-empty-{Guid.NewGuid():N}@example.test");
        string token;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User user = await context.DomainUsers.SingleAsync(u => u.Id == userId);
            AvatarSource source = user.CaptureAvatarSource(
                AvatarSourceKind.SteamPlatform,
                [1],
                "image/webp",
                _factory.Clock);
            token = source.Token;
            await context.SaveChangesAsync();

            // Bypasses the domain's non-empty invariant deliberately: the invariant makes this
            // branch unreachable through the aggregate, but the proxy still guards it defensively.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE avatar_sources SET bytes = '' WHERE token = {token}");
        }

        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using HttpResponseMessage response = await client.GetAsync($"/a/{token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void ProxyPath_IsRedactedByTheApplicationLogSanitizer()
    {
        Assert.Equal(
            "/a/REDACTED",
            FriendPathLogSanitizer.Sanitize(new Microsoft.AspNetCore.Http.PathString("/a/some-token-value")));
    }

    private async Task<string> CaptureAsync(UserId userId, byte[] bytes, string contentType)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User user = await context.DomainUsers.SingleAsync(u => u.Id == userId);
        AvatarSource source = user.CaptureAvatarSource(
            AvatarSourceKind.SteamPlatform,
            bytes,
            contentType,
            _factory.Clock);
        await context.SaveChangesAsync();
        return source.Token;
    }

    private static string? Single(HttpResponseMessage response, string headerName) =>
        response.Headers.TryGetValues(headerName, out IEnumerable<string>? values)
            ? values.Single()
            : response.Content.Headers.TryGetValues(headerName, out IEnumerable<string>? contentValues)
                ? contentValues.Single()
                : null;
}
