using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// A-064's transport-entry exchange (PHASE4_DESIGN.md §8.1, §9.1): a non-mutating GET that issues a
/// path-scoped protected cookie and redirects to a tokenless SPA route, never disclosing the token in
/// the redirect target, and never touching the database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GuestExchangeTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Get_DoesNotMutateDomainState_A064()
    {
        (Session session, string token) = await SeedSessionWithGuestLinkAsync();
        using HttpClient client = CreateExchangeClient();

        int participationsBefore = await CountParticipationsAsync(session.Id);
        HttpResponseMessage response = await client.GetAsync($"/g/{token}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(participationsBefore, await CountParticipationsAsync(session.Id));
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(context.GuestIdentities.Where(g => g.SessionId == session.Id));
    }

    [Fact]
    public async Task ValidToken_SetsPathScopedHttpOnlyCookieAndRedirectsTokenlessly_A065()
    {
        (_, string token) = await SeedSessionWithGuestLinkAsync();
        using HttpClient client = CreateExchangeClient();

        HttpResponseMessage response = await client.GetAsync($"/g/{token}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        string? location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.StartsWith("/guest/", location);
        Assert.DoesNotContain(token, location, StringComparison.Ordinal);

        SetCookieHeaderValue cookie = SetCookiesOf(response).Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        Assert.True(cookie.HttpOnly);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
        Assert.StartsWith("/api/v1/guest/", cookie.Path.Value);
    }

    [Fact]
    public async Task RevokedCanceledOrEndedLinks_ServeTheTerminalNoticeOnTheTokenlessRoute_D044_D174()
    {
        (Session session, string token) = await SeedSessionWithGuestLinkAsync();
        await using (AsyncServiceScope cancelScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = cancelScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Session tracked = await context.Sessions.Include(s => s.GuestLinks)
                .SingleAsync(s => s.Id == session.Id);
            tracked.Cancel(_factory.Clock);
            _ = tracked.DequeueEvents();
            await context.SaveChangesAsync();
        }

        using HttpClient client = CreateExchangeClient();
        HttpResponseMessage exchange = await client.GetAsync($"/g/{token}");
        Assert.Equal(HttpStatusCode.Found, exchange.StatusCode);
        Assert.StartsWith("/guest/", exchange.Headers.Location!.ToString());

        SetCookieHeaderValue cookie = SetCookiesOf(exchange).Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        Guid guestLinkId = session.GuestLinks[0].Id.Value;
        using JsonDocument view = await FetchGuestSessionAsync(client, guestLinkId, CookieHeader(cookie));
        Assert.Equal("canceled", view.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task UnknownOrForgedToken_RedirectsToTheInvalidNoticeWithoutACookie_A064()
    {
        using HttpClient client = CreateExchangeClient();

        HttpResponseMessage response = await client.GetAsync($"/g/{Guid.NewGuid():N}forged");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/guest/invalid", response.Headers.Location!.ToString());
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
    }

    [Fact]
    public async Task MultipleLinksRemainIndependentlyUsable_D115()
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedContext = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserId organizerId = await _factory.SeedUserAsync(email: "multi-link-organizer@example.test");
        SessionDetails details = new(
            SessionTitle.Create("Multi-link session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromDays(1),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: true);
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        (GuestLink linkA, string tokenA) = session.CreateGuestLink(_factory.Clock);
        (GuestLink linkB, string tokenB) = session.CreateGuestLink(_factory.Clock);
        _ = session.DequeueEvents();
        seedContext.Sessions.Add(session);
        await seedContext.SaveChangesAsync();

        using HttpClient client = CreateExchangeClient();
        HttpResponseMessage exchangeA = await client.GetAsync($"/g/{tokenA}");
        HttpResponseMessage exchangeB = await client.GetAsync($"/g/{tokenB}");

        string cookieNameA = SetCookiesOf(exchangeA).Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal)).Name.Value!;
        string cookieNameB = SetCookiesOf(exchangeB).Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal)).Name.Value!;
        Assert.NotEqual(cookieNameA, cookieNameB);
        Assert.Equal($"pu.g.{linkA.Id.Value:N}", cookieNameA);
        Assert.Equal($"pu.g.{linkB.Id.Value:N}", cookieNameB);
    }

    [Fact]
    public async Task GuestAccessRequiresNoAgeGate_D124()
    {
        (Session session, string token) = await SeedSessionWithGuestLinkAsync();
        using HttpClient client = CreateExchangeClient();
        HttpResponseMessage exchange = await client.GetAsync($"/g/{token}");
        SetCookieHeaderValue cookie = SetCookiesOf(exchange).Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));

        using JsonDocument view = await FetchGuestSessionAsync(
            client, session.GuestLinks[0].Id.Value, CookieHeader(cookie));

        Assert.Equal("live", view.RootElement.GetProperty("state").GetString());
    }

    // ---- helpers -------------------------------------------------------------------------------

    private HttpClient CreateExchangeClient() =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private async Task<(Session Session, string Token)> SeedSessionWithGuestLinkAsync()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"guest-exchange-{Guid.NewGuid():N}@example.test");
        SessionDetails details = new(
            SessionTitle.Create("Exchange test session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromDays(1),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: true);
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        (GuestLink _, string token) = session.CreateGuestLink(_factory.Clock);
        _ = session.DequeueEvents();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return (session, token);
    }

    private async Task<int> CountParticipationsAsync(SessionId sessionId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.SessionParticipations.CountAsync(p => p.SessionId == sessionId);
    }

    private static async Task<JsonDocument> FetchGuestSessionAsync(HttpClient client, Guid guestLinkId, string cookieHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/session");
        request.Headers.Add(HeaderNames.Cookie, cookieHeader);
        HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    internal static IReadOnlyList<SetCookieHeaderValue> SetCookiesOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues(HeaderNames.SetCookie, out IEnumerable<string>? values)
            ? values.Select(value => SetCookieHeaderValue.Parse(value)).ToArray()
            : [];

    internal static string CookieHeader(SetCookieHeaderValue cookie) =>
        $"{cookie.Name}={cookie.Value}";
}
