using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;
using SixLabors.ImageSharp;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// D-224: an allowlisted link-preview crawler on <c>GET /g/{token}</c> gets Open Graph metadata and
/// a server-rendered card instead of the bearer's cookie-and-redirect exchange. Everything that is
/// not a live link unfurls as one generic notice, so an unknown token discloses nothing a revoked
/// one does not.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GuestUnfurlTests : IAsyncLifetime, IDisposable
{
    private const string CrawlerUserAgent = "Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
    private const string MarkupTitle = "Raid <b>&\"night\"</b>";

    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task CrawlerOnALiveSessionLink_GetsEscapedOpenGraphMetadataAndNoCookie_D224()
    {
        (_, string token) = await SeedSessionWithGuestLinkAsync(MarkupTitle);
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await FetchAsync(client, $"/g/{token}", CrawlerUserAgent);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("noindex", response.Headers.GetValues("X-Robots-Tag").Single());

        Assert.Contains(
            "<meta property=\"og:title\" content=\"Raid &lt;b&gt;&amp;&quot;night&quot;&lt;/b&gt;\">",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", body, StringComparison.Ordinal);
        Assert.Contains("<meta property=\"og:type\" content=\"website\">", body, StringComparison.Ordinal);
        Assert.Contains("<meta property=\"og:site_name\" content=\"PartyUp\">", body, StringComparison.Ordinal);
        Assert.Contains(
            "<meta name=\"twitter:card\" content=\"summary_large_image\">",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            $"<meta property=\"og:image\" content=\"http://localhost:5173/g/{token}/card.png\">",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            $"<link rel=\"canonical\" href=\"http://localhost:5173/g/{token}\">",
            body,
            StringComparison.Ordinal);
        Assert.Contains("going", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonCrawlerOnTheSameLink_StillGetsTheCookieAndRedirect_D224()
    {
        (_, string token) = await SeedSessionWithGuestLinkAsync(MarkupTitle);
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await FetchAsync(client, $"/g/{token}", BrowserUserAgent);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("/guest/", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            GuestExchangeTests.SetCookiesOf(response),
            cookie => cookie.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CrawlerOnARevokedLink_GetsTheGenericNoticeWithNoTitleLeak_D224()
    {
        (Session session, string token) = await SeedSessionWithGuestLinkAsync(MarkupTitle);
        await RevokeAsync(session);
        using HttpClient client = CreateClient();

        string body = await (await FetchAsync(client, $"/g/{token}", CrawlerUserAgent)).Content.ReadAsStringAsync();

        Assert.Contains("This invite is no longer available", body, StringComparison.Ordinal);
        Assert.Contains("<title>Invite link</title>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Raid", body, StringComparison.Ordinal);
        Assert.DoesNotContain("going", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrawlerOnAnUnknownToken_GetsTheSameBodyAsARevokedLink_D224()
    {
        // The revoked link's organizer is pt-BR: an unknown token has no organizer and therefore no
        // locale, so the generic card can only be indistinguishable if it is always English.
        (Session session, string revokedToken) = await SeedSessionWithGuestLinkAsync(MarkupTitle, "pt-BR");
        await RevokeAsync(session);
        string unknownToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using HttpClient client = CreateClient();

        string revoked = await (await FetchAsync(client, $"/g/{revokedToken}", CrawlerUserAgent))
            .Content.ReadAsStringAsync();
        string unknown = await (await FetchAsync(client, $"/g/{unknownToken}", CrawlerUserAgent))
            .Content.ReadAsStringAsync();
        byte[] revokedCard = await (await FetchAsync(client, $"/g/{revokedToken}/card.png", CrawlerUserAgent))
            .Content.ReadAsByteArrayAsync();
        byte[] unknownCard = await (await FetchAsync(client, $"/g/{unknownToken}/card.png", CrawlerUserAgent))
            .Content.ReadAsByteArrayAsync();

        // The only difference either body may carry is the token echoed back in its own URLs.
        Assert.Equal(
            revoked.Replace(revokedToken, "TOKEN", StringComparison.Ordinal),
            unknown.Replace(unknownToken, "TOKEN", StringComparison.Ordinal));
        Assert.Contains("This invite is no longer available", revoked, StringComparison.Ordinal);
        Assert.DoesNotContain("disponível", revoked, StringComparison.Ordinal);
        Assert.Equal(revokedCard, unknownCard);
    }

    [Fact]
    public async Task CrawlerOnALivePtBrSessionLink_GetsTheOrganizerLocaleDescription_D224()
    {
        (_, string token) = await SeedSessionWithGuestLinkAsync("Noite de jogos", "pt-BR");
        using HttpClient client = CreateClient();

        string body = await (await FetchAsync(client, $"/g/{token}", CrawlerUserAgent)).Content.ReadAsStringAsync();

        Assert.Contains("<title>Noite de jogos</title>", body, StringComparison.Ordinal);
        Assert.Contains("confirmado", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" going", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrawlerGetDoesNotMutateDomainState_A064_D224()
    {
        (Session session, string token) = await SeedSessionWithGuestLinkAsync(MarkupTitle);
        using HttpClient client = CreateClient();

        (int participations, GuestLinkState state) before = await SnapshotAsync(session.Id);
        HttpResponseMessage document = await FetchAsync(client, $"/g/{token}", CrawlerUserAgent);
        HttpResponseMessage card = await FetchAsync(client, $"/g/{token}/card.png", CrawlerUserAgent);

        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);
        Assert.Equal(before, await SnapshotAsync(session.Id));
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(context.GuestIdentities.Where(guest => guest.SessionId == session.Id));
    }

    [Fact]
    public async Task CrawlerOnALivePollLink_GetsThePollTitleAndTheVoteInvitation_D224()
    {
        string token = await SeedPollWithGuestLinkAsync("Weeknight poll");
        using HttpClient client = CreateClient();

        string body = await (await FetchAsync(client, $"/g/{token}", CrawlerUserAgent)).Content.ReadAsStringAsync();

        Assert.Contains("<title>Weeknight poll</title>", body, StringComparison.Ordinal);
        Assert.Contains("Vote on when to play", body, StringComparison.Ordinal);
        Assert.Contains("closes ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CardPngForALiveLink_Is1200x630AndPrivatelyCacheable_D224()
    {
        (_, string token) = await SeedSessionWithGuestLinkAsync(MarkupTitle);
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await FetchAsync(client, $"/g/{token}/card.png", CrawlerUserAgent);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromMinutes(5), response.Headers.CacheControl?.MaxAge);
        Assert.Equal("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));

        using Image image = Image.Load(bytes);
        Assert.Equal(1200, image.Width);
        Assert.Equal(630, image.Height);
    }

    [Fact]
    public async Task CardPngForTwoUnknownTokens_IsTheSameGenericImage_D224()
    {
        using HttpClient client = CreateClient();

        byte[] first = await (await FetchAsync(
            client, $"/g/{Guid.NewGuid():N}/card.png", CrawlerUserAgent)).Content.ReadAsByteArrayAsync();
        byte[] second = await (await FetchAsync(
            client, $"/g/{Guid.NewGuid():N}/card.png", CrawlerUserAgent)).Content.ReadAsByteArrayAsync();

        Assert.Equal(first, second);
        using Image image = Image.Load(first);
        Assert.Equal(1200, image.Width);
        Assert.Equal(630, image.Height);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> FetchAsync(HttpClient client, string path, string userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(HeaderNames.UserAgent, userAgent);
        return client.SendAsync(request);
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private async Task<(int Participations, GuestLinkState State)> SnapshotAsync(SessionId sessionId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int participations = await context.SessionParticipations.CountAsync(p => p.SessionId == sessionId);
        Session session = await context.Sessions
            .AsNoTracking()
            .Include(s => s.GuestLinks)
            .SingleAsync(s => s.Id == sessionId);
        return (participations, session.GuestLinks[0].State);
    }

    private async Task RevokeAsync(Session session)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Session tracked = await context.Sessions
            .Include(s => s.GuestLinks)
            .SingleAsync(s => s.Id == session.Id);
        tracked.RevokeGuestLink(tracked.GuestLinks[0].Id, _factory.Clock);
        _ = tracked.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task<(Session Session, string Token)> SeedSessionWithGuestLinkAsync(
        string title,
        string organizerLocale = "en")
    {
        UserId organizerId = await _factory.SeedUserAsync(
            email: $"guest-unfurl-{Guid.NewGuid():N}@example.test",
            locale: organizerLocale);
        SessionDetails details = new(
            SessionTitle.Create(title),
            _factory.Clock.GetCurrentInstant() + Duration.FromDays(1),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            SessionGame.FreeText(GameName.Create("Deep Rock Galactic")),
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

    private async Task<string> SeedPollWithGuestLinkAsync(string title)
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-unfurl-{Guid.NewGuid():N}@example.test");
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        Poll poll = Poll.Create(
            organizerId,
            new PollDetails(
                SessionTitle.Create(title),
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
        (GuestLink _, string token) = poll.CreateGuestLink(_factory.Clock);
        _ = poll.DequeueEvents();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Polls.Add(poll);
        await context.SaveChangesAsync();
        return token;
    }
}
