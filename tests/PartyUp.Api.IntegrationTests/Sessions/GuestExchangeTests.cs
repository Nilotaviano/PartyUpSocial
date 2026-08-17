using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.Security;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
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
    public async Task StaleGuestCookieIsClearedWithThePathItWasSetWith_A065_D174()
    {
        // §9.1: browsers only clear a cookie when the delete Set-Cookie matches both name AND path.
        // The stale cookie carries its own guest link id in its name (pu.g.{id:N}), and credentials are
        // originally set with Path=/api/v1/guest/{id:N}, so the delete cookie must reconstruct and
        // reuse that same path — a Path-less delete (or the request path) never actually clears it.
        (_, string token) = await SeedSessionWithGuestLinkAsync();
        using HttpClient client = CreateExchangeClient();

        Guid staleGuestLinkId = Guid.NewGuid();
        Instant now = _factory.Clock.GetCurrentInstant();
        GuestLinkCredentialProtector protector;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            protector = scope.ServiceProvider.GetRequiredService<GuestLinkCredentialProtector>();
        }

        string staleValue = protector.Protect(new GuestLinkCredential(
            GuestLinkId.From(staleGuestLinkId),
            SessionId.From(Guid.NewGuid()),
            GuestCredentialMode.Active,
            now - Duration.FromDays(2),
            now - Duration.FromDays(1)));
        string staleCookieName = $"pu.g.{staleGuestLinkId:N}";

        var request = new HttpRequestMessage(HttpMethod.Get, $"/g/{token}");
        request.Headers.Add(HeaderNames.Cookie, $"{staleCookieName}={staleValue}");
        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        SetCookieHeaderValue deleteCookie = SetCookiesOf(response).Single(c => c.Name.Value == staleCookieName);
        Assert.Equal($"/api/v1/guest/{staleGuestLinkId:N}", deleteCookie.Path.Value);
        Assert.Equal(TimeSpan.Zero, deleteCookie.MaxAge);
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
            visibility: ContentVisibility.Private);
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

    // ---- ticket-P6-S4b: the /g/{token} poll-owned branch ---------------------------------------

    /// <summary>
    /// The poll branch (PHASE6_DESIGN.md §4): a poll-owned token mints a poll-purpose credential and
    /// redirects to the SAME tokenless <c>/guest/{linkId}</c> route the session branch uses — never a
    /// distinguishable path shape a bearer or observer could use to tell polls and sessions apart.
    /// </summary>
    [Fact]
    public async Task PollOwnedToken_SetsAPollPurposeCookieAndRedirectsToTheSameTokenlessRoute_D125()
    {
        (PollId pollId, GuestLinkId linkId, string token) = await SeedPollWithGuestLinkAsync();
        using HttpClient client = CreateExchangeClient();

        HttpResponseMessage response = await client.GetAsync($"/g/{token}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        string? location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.Equal($"/guest/{linkId.Value:N}", location);
        Assert.DoesNotContain(token, location, StringComparison.Ordinal);

        SetCookieHeaderValue cookie = SetCookiesOf(response).Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        Assert.Equal($"pu.g.{linkId.Value:N}", cookie.Name.Value);
        Assert.True(cookie.HttpOnly);
        Assert.StartsWith("/api/v1/guest/", cookie.Path.Value);

        using JsonDocument view = await FetchGuestPollAsync(client, linkId.Value, CookieHeader(cookie));
        Assert.Equal("live", view.RootElement.GetProperty("state").GetString());
        Assert.Equal(pollId.Value, view.RootElement.GetProperty("pollId").GetGuid());
        Assert.DoesNotContain(token, await FetchGuestPollBodyAsync(client, linkId.Value, CookieHeader(cookie)), StringComparison.Ordinal);
    }

    /// <summary>A session-purpose credential cannot decrypt on a poll route, and vice versa (PHASE6_DESIGN.md §4).</summary>
    [Fact]
    public async Task CrossPurposeCredential_IsRejectedOnTheOtherArmsRoute_StructurallyFailClosed()
    {
        (Session session, string sessionToken) = await SeedSessionWithGuestLinkAsync();
        (PollId _, GuestLinkId pollLinkId, string pollToken) = await SeedPollWithGuestLinkAsync();
        using HttpClient client = CreateExchangeClient();

        HttpResponseMessage sessionExchange = await client.GetAsync($"/g/{sessionToken}");
        SetCookieHeaderValue sessionCookie = SetCookiesOf(sessionExchange)
            .Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));

        HttpResponseMessage pollExchange = await client.GetAsync($"/g/{pollToken}");
        SetCookieHeaderValue pollCookie = SetCookiesOf(pollExchange)
            .Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));

        Guid sessionGuestLinkId = session.GuestLinks[0].Id.Value;

        // The session-typed cookie on the poll route.
        var pollRouteRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{sessionGuestLinkId:N}/poll");
        pollRouteRequest.Headers.Add(HeaderNames.Cookie, CookieHeader(sessionCookie));
        HttpResponseMessage crossOne = await client.SendAsync(pollRouteRequest);
        Assert.Equal(HttpStatusCode.NotFound, crossOne.StatusCode);
        Assert.Contains("poll_not_found", await crossOne.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The poll-typed cookie on the session route.
        var sessionRouteRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{pollLinkId.Value:N}/session");
        sessionRouteRequest.Headers.Add(HeaderNames.Cookie, CookieHeader(pollCookie));
        HttpResponseMessage crossTwo = await client.SendAsync(sessionRouteRequest);
        Assert.Equal(HttpStatusCode.NotFound, crossTwo.StatusCode);
        Assert.Contains("session_not_found", await crossTwo.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private async Task<(PollId PollId, GuestLinkId LinkId, string Token)> SeedPollWithGuestLinkAsync()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"guest-poll-exchange-{Guid.NewGuid():N}@example.test");
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        Poll poll = Poll.Create(
            organizerId,
            new PollDetails(
                SessionTitle.Create("Poll exchange test"),
                Platform.NotApplicable,
                null,
                SessionNotes.Empty,
                minAttendance: 1,
                maxCapacity: null,
                visibility: ContentVisibility.Private),
            PollDeadlineChoice.Hours24,
            PollSpecs.Of(baseTime, baseTime + Duration.FromHours(1)),
            [],
            _factory.Clock);
        (GuestLink link, string token) = poll.CreateGuestLink(_factory.Clock);
        _ = poll.DequeueEvents();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Polls.Add(poll);
        await context.SaveChangesAsync();
        return (poll.Id, link.Id, token);
    }

    private static async Task<JsonDocument> FetchGuestPollAsync(HttpClient client, Guid guestLinkId, string cookieHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/poll");
        request.Headers.Add(HeaderNames.Cookie, cookieHeader);
        HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> FetchGuestPollBodyAsync(HttpClient client, Guid guestLinkId, string cookieHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/poll");
        request.Headers.Add(HeaderNames.Cookie, cookieHeader);
        HttpResponseMessage response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
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
            visibility: ContentVisibility.Private);
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
