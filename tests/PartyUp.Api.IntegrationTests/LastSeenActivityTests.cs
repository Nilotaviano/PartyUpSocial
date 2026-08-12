using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

/// <summary>
/// PHASE8_DESIGN.md §2 Q1: <c>users.last_seen_at</c> is touched on authenticated requests but
/// throttled to a ten-minute write floor, so it is not a per-request write. The column value itself
/// is the evidence — a second request inside the floor must leave the first request's instant
/// standing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LastSeenActivityTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task LastSeen_IsTouchedOncePerTenMinuteFloor_D178()
    {
        UserId userId = await _factory.SeedUserAsync(email: "last-seen@example.test");
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, userId);

        Assert.Null(await LastSeenAsync(userId));

        Instant firstRequestAt = _factory.Clock.GetCurrentInstant();
        await GetMeAsync(client, cookie);
        Assert.Equal(firstRequestAt, await LastSeenAsync(userId));

        // Inside the floor: a second authenticated request writes nothing.
        _factory.Clock.Advance(LastSeenTouchMiddleware.WriteFloor - Duration.FromMinutes(1));
        await GetMeAsync(client, cookie);
        Assert.Equal(firstRequestAt, await LastSeenAsync(userId));

        // Past the floor: the next request moves it.
        _factory.Clock.Advance(Duration.FromMinutes(2));
        Instant thirdRequestAt = _factory.Clock.GetCurrentInstant();
        await GetMeAsync(client, cookie);
        Assert.Equal(thirdRequestAt, await LastSeenAsync(userId));
    }

    /// <summary>
    /// The unauthenticated path: no projection is resolved, so the middleware writes nothing and
    /// throws nothing — an anonymous request is served exactly as it was before the touch existed. An
    /// authenticated request runs first so the column already holds a real value; the anonymous
    /// request that follows must leave that value exactly as it found it, proving the anonymous path
    /// is a no-op rather than merely that a never-touched column stays null.
    /// </summary>
    [Fact]
    public async Task LastSeen_IsNotTouchedByAnAnonymousRequest_D178()
    {
        UserId userId = await _factory.SeedUserAsync(email: "last-seen-anonymous@example.test");
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, userId);

        Instant authenticatedRequestAt = _factory.Clock.GetCurrentInstant();
        await GetMeAsync(client, cookie);
        Assert.Equal(authenticatedRequestAt, await LastSeenAsync(userId));

        _factory.Clock.Advance(LastSeenTouchMiddleware.WriteFloor + Duration.FromMinutes(1));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(authenticatedRequestAt, await LastSeenAsync(userId));
    }

    private static async Task GetMeAsync(HttpClient client, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Add(HeaderNames.Cookie, cookie);
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Instant?> LastSeenAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.DomainUsers
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => EF.Property<Instant?>(user, UserActivityWriter.LastSeenProperty))
            .SingleAsync();
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
}
