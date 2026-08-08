using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// The poll guest HTTP arm (PHASE6_DESIGN.md §4, ticket-P6-S4b): the parallel poll credential
/// protector, the guest poll routes on the shipped <c>/guest/{guestLinkId}</c> group, cross-credential
/// fail-closed behavior in both directions, and the post-finalization re-parent handover.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollGuestHttpTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    /// <summary>D-017: claim needs only a display name; a claimed guest can then vote — over HTTP, not the Application layer directly.</summary>
    [Fact]
    public async Task ClaimThenVote_OverHttp_Succeeds_D017_D114()
    {
        (SessionHttpSession organizer, Guid pollId, Guid optionId, Guid guestLinkId, string token) =
            await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();
        string cookieHeader = await ExchangeAsync(guestClient, token);

        using JsonDocument claimed = await ClaimAsync(guestClient, guestLinkId, cookieHeader, "HTTP Guest One");
        Assert.Equal("live", claimed.RootElement.GetProperty("state").GetString());
        Assert.Equal("HTTP Guest One", claimed.RootElement.GetProperty("displayName").GetString());
        Guid guestIdentityId = claimed.RootElement.GetProperty("guestIdentityId").GetGuid();

        HttpResponseMessage voted = await AntiforgeryTestClient.SendAsync(
            guestClient,
            HttpMethod.Put,
            $"/api/v1/guest/{guestLinkId:N}/poll/options/{optionId:D}/vote",
            JsonContent.Create(new { choice = "available" }),
            cookieHeader);
        using JsonDocument votedBody = await SessionEndpointTestSupport.SuccessJsonAsync(voted);
        JsonElement ownOption = votedBody.RootElement.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("id").GetGuid() == optionId);
        Assert.Equal("available", ownOption.GetProperty("viewerVote").GetString());

        // The registered voter-grid view (participant-identical, D-030/D-114) shows the same guest row.
        using JsonDocument registeredView = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizer.GetAsync($"/api/v1/polls/{pollId:D}"));
        Assert.Contains(
            registeredView.RootElement.GetProperty("roster").EnumerateArray(),
            r => r.TryGetProperty("guestIdentityId", out JsonElement g) &&
                g.ValueKind != JsonValueKind.Null &&
                g.GetGuid() == guestIdentityId);
    }

    /// <summary>D-017: an unclaimed link voting is an explicit 409, mirroring the session contract.</summary>
    [Fact]
    public async Task VoteBeforeClaim_OverHttp_Is409_D017()
    {
        (_, _, Guid optionId, Guid guestLinkId, string token) = await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();
        string cookieHeader = await ExchangeAsync(guestClient, token);

        HttpResponseMessage voted = await AntiforgeryTestClient.SendAsync(
            guestClient,
            HttpMethod.Put,
            $"/api/v1/guest/{guestLinkId:N}/poll/options/{optionId:D}/vote",
            JsonContent.Create(new { choice = "available" }),
            cookieHeader);

        Assert.Equal(HttpStatusCode.Conflict, voted.StatusCode);
        Assert.Contains("guest_link_not_claimed", await voted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>D-033: a claimed guest's suggestion lands as a real option, resolved to their identity.</summary>
    [Fact]
    public async Task Suggest_OverHttp_LandsAsAnOption_D033()
    {
        (_, Guid pollId, _, Guid guestLinkId, string token) = await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();
        string cookieHeader = await ExchangeAsync(guestClient, token);
        await ClaimAsync(guestClient, guestLinkId, cookieHeader, "Suggesting Guest");

        Instant suggestedTime = _factory.Clock.GetCurrentInstant() + Duration.FromHours(9);
        HttpResponseMessage suggested = await AntiforgeryTestClient.SendAsync(
            guestClient,
            HttpMethod.Post,
            $"/api/v1/guest/{guestLinkId:N}/poll/options",
            JsonContent.Create(new { startTime = SessionEndpointTestSupport.Iso(suggestedTime) }),
            cookieHeader);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(suggested);
        Assert.Contains(
            body.RootElement.GetProperty("options").EnumerateArray(),
            o => o.GetProperty("startTime").GetString() == SessionEndpointTestSupport.Iso(suggestedTime));
    }

    /// <summary>D-126: renaming twice through the same link is last-write-wins over HTTP, no conflict.</summary>
    [Fact]
    public async Task Rename_TwiceOverHttp_LastWriteWins_D126()
    {
        (_, _, _, Guid guestLinkId, string token) = await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();
        string cookieHeader = await ExchangeAsync(guestClient, token);
        await ClaimAsync(guestClient, guestLinkId, cookieHeader, "First Name");

        HttpResponseMessage renamed = await AntiforgeryTestClient.SendAsync(
            guestClient,
            HttpMethod.Put,
            $"/api/v1/guest/{guestLinkId:N}/poll/display-name",
            JsonContent.Create(new { displayName = "Second Name" }),
            cookieHeader);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(renamed);
        Assert.Equal("Second Name", body.RootElement.GetProperty("displayName").GetString());
    }

    /// <summary>A session-typed credential structurally cannot decrypt on a poll route (different Data Protection purpose) — 404, fail closed.</summary>
    [Fact]
    public async Task SessionCredential_OnAPollRoute_Is404_FailClosed()
    {
        (Session session, string sessionToken) = await SeedSessionWithGuestLinkAsync();
        (_, _, _, _, string _) = await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();
        string sessionCookie = await ExchangeAsync(guestClient, sessionToken);
        Guid sessionGuestLinkId = session.GuestLinks[0].Id.Value;

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{sessionGuestLinkId:N}/poll");
        request.Headers.Add(HeaderNames.Cookie, sessionCookie);
        HttpResponseMessage response = await guestClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("poll_not_found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>The reverse direction: a poll-typed credential on the shipped guest session route.</summary>
    [Fact]
    public async Task PollCredential_OnASessionRoute_Is404_FailClosed()
    {
        (_, _, _, Guid guestLinkId, string token) = await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();
        string pollCookie = await ExchangeAsync(guestClient, token);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/session");
        request.Headers.Add(HeaderNames.Cookie, pollCookie);
        HttpResponseMessage response = await guestClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("session_not_found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.4: a poll past its selection window (never finalized) serves the pollExpired terminal
    /// notice. The clamp that makes active-mode expiry equal <c>selection_window_end</c> means the
    /// ORIGINAL active credential expires at the exact same instant the poll does — reaching the
    /// notice needs the guest to revisit their <c>/g/{token}</c> link, which mints a fresh, bounded
    /// terminal credential the same way the shipped session arm's stale/terminal visits already do.
    /// </summary>
    [Fact]
    public async Task ExpiredPoll_ServesThePollExpiredTerminalNotice()
    {
        (_, Guid pollId, _, Guid guestLinkId, string token) = await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();

        _factory.Clock.Advance(Duration.FromHours(73)); // 24h deadline + 48h selection window + margin

        string cookieHeader = await ExchangeAsync(guestClient, token);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/poll");
        request.Headers.Add(HeaderNames.Cookie, cookieHeader);
        HttpResponseMessage response = await guestClient.SendAsync(request);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(response);
        Assert.Equal("pollExpired", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(pollId, body.RootElement.GetProperty("pollId").GetGuid());
    }

    /// <summary>
    /// The finalization handover end-to-end (ticket-P6-S4b): mint, claim, vote, organizer finalizes —
    /// the poll credential's link has re-parented onto the created session (tokens preserved, D-048),
    /// so the poll route now serves the finalized notice carrying the re-exchange URL; re-exchanging
    /// the same preserved raw token mints a SESSION credential that works on the session routes. No
    /// silent cookie upgrade — the poll-typed cookie is never itself flipped into a session credential.
    /// </summary>
    [Fact]
    public async Task FinalizationHandover_MintClaimVoteFinalize_ThenReExchangeMintsASessionCredential()
    {
        (SessionHttpSession organizer, Guid pollId, Guid optionId, Guid guestLinkId, string token) =
            await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();
        string pollCookieHeader = await ExchangeAsync(guestClient, token);

        await ClaimAsync(guestClient, guestLinkId, pollCookieHeader, "Handover Guest");
        HttpResponseMessage voted = await AntiforgeryTestClient.SendAsync(
            guestClient,
            HttpMethod.Put,
            $"/api/v1/guest/{guestLinkId:N}/poll/options/{optionId:D}/vote",
            JsonContent.Create(new { choice = "available" }),
            pollCookieHeader);
        Assert.Equal(HttpStatusCode.OK, voted.StatusCode);

        _factory.Clock.Advance(Duration.FromHours(25));
        HttpResponseMessage finalize = await organizer.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/polls/{pollId:D}/finalize", JsonContent.Create(new { optionId }));
        using JsonDocument finalizeBody = await SessionEndpointTestSupport.SuccessJsonAsync(finalize);
        Guid sessionId = finalizeBody.RootElement.GetProperty("sessionId").GetGuid();

        // The still-held poll credential no longer matches a poll-owned link.
        var noticeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/poll");
        noticeRequest.Headers.Add(HeaderNames.Cookie, pollCookieHeader);
        HttpResponseMessage notice = await guestClient.SendAsync(noticeRequest);
        using JsonDocument noticeBody = await SessionEndpointTestSupport.SuccessJsonAsync(notice);
        Assert.Equal("finalized", noticeBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(sessionId, noticeBody.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Equal("/g/{token}", noticeBody.RootElement.GetProperty("reExchangeUrl").GetString());
        Assert.DoesNotContain(token, await notice.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Re-exchanging the preserved raw token now mints a SESSION credential for the same link —
        // never a silent upgrade of the poll cookie in place.
        HttpResponseMessage reExchange = await guestClient.GetAsync($"/g/{token}");
        Assert.Equal(HttpStatusCode.Found, reExchange.StatusCode);
        Assert.Equal($"/guest/{guestLinkId:N}", reExchange.Headers.Location!.ToString());
        SetCookieHeaderValue sessionCookie = GuestExchangeTests.SetCookiesOf(reExchange)
            .Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        Assert.Equal($"pu.g.{guestLinkId:N}", sessionCookie.Name.Value);
        string sessionCookieHeader = GuestExchangeTests.CookieHeader(sessionCookie);

        var sessionRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/session");
        sessionRequest.Headers.Add(HeaderNames.Cookie, sessionCookieHeader);
        HttpResponseMessage sessionView = await guestClient.SendAsync(sessionRequest);
        using JsonDocument sessionViewBody = await SessionEndpointTestSupport.SuccessJsonAsync(sessionView);
        Assert.Equal("live", sessionViewBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(sessionId, sessionViewBody.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Equal("Handover Guest", sessionViewBody.RootElement.GetProperty("displayName").GetString());

        // The poll-typed cookie's own credential purpose never silently changed: it still 404s on the
        // poll route the same way it always would once the underlying link is gone from the poll.
        var stalePollRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/poll");
        stalePollRequest.Headers.Add(HeaderNames.Cookie, pollCookieHeader);
        HttpResponseMessage stalePoll = await guestClient.SendAsync(stalePollRequest);
        Assert.Equal(HttpStatusCode.OK, stalePoll.StatusCode);
        using JsonDocument stalePollBody = JsonDocument.Parse(await stalePoll.Content.ReadAsStringAsync());
        Assert.Equal("finalized", stalePollBody.RootElement.GetProperty("state").GetString());
    }

    /// <summary>A-015: the raw token appears only in the mint response and the /g/ redirect Location — never in any guest poll response body.</summary>
    [Fact]
    public async Task MintedToken_NeverAppearsInAnyGuestPollResponseBody_A015()
    {
        (_, _, Guid optionId, Guid guestLinkId, string token) = await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();
        string cookieHeader = await ExchangeAsync(guestClient, token);

        using JsonDocument claimed = await ClaimAsync(guestClient, guestLinkId, cookieHeader, "Redaction Guest");
        Assert.DoesNotContain(token, claimed.RootElement.ToString(), StringComparison.Ordinal);

        HttpResponseMessage voted = await AntiforgeryTestClient.SendAsync(
            guestClient,
            HttpMethod.Put,
            $"/api/v1/guest/{guestLinkId:N}/poll/options/{optionId:D}/vote",
            JsonContent.Create(new { choice = "available" }),
            cookieHeader);
        string voteBody = await voted.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, voteBody, StringComparison.Ordinal);

        var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/poll");
        getRequest.Headers.Add(HeaderNames.Cookie, cookieHeader);
        HttpResponseMessage getResponse = await guestClient.SendAsync(getRequest);
        string getBody = await getResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, getBody, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private HttpClient CreateGuestClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task<string> ExchangeAsync(HttpClient client, string token)
    {
        HttpResponseMessage response = await client.GetAsync($"/g/{token}");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        SetCookieHeaderValue cookie = GuestExchangeTests.SetCookiesOf(response)
            .Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        return GuestExchangeTests.CookieHeader(cookie);
    }

    private static async Task<JsonDocument> ClaimAsync(
        HttpClient client, Guid guestLinkId, string cookieHeader, string displayName)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.SendAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/guest/{guestLinkId:N}/poll/claim",
            JsonContent.Create(new { displayName }),
            cookieHeader);
        return await SessionEndpointTestSupport.SuccessJsonAsync(response);
    }

    private async Task<(SessionHttpSession Organizer, Guid PollId, Guid OptionId, Guid GuestLinkId, string Token)>
        SeedPollWithGuestLinkAsync()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-guest-http-{Guid.NewGuid():N}@example.test");
        SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);

        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Guest HTTP poll",
                optionStartTimes = new[]
                {
                    SessionEndpointTestSupport.Iso(baseTime),
                    SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(1)),
                },
                isPrivate = true,
                minAttendance = 1,
                guestLinkCount = 1,
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
        Guid optionId = body.RootElement.GetProperty("poll").GetProperty("options")[0].GetProperty("id").GetGuid();
        Guid guestLinkId = body.RootElement.GetProperty("guestLinks")[0].GetProperty("guestLinkId").GetGuid();
        string url = body.RootElement.GetProperty("guestLinks")[0].GetProperty("url").GetString()!;
        string token = url[(url.LastIndexOf("/g/", StringComparison.Ordinal) + 3)..];

        return (organizerSession, pollId, optionId, guestLinkId, token);
    }

    private async Task<(Session Session, string Token)> SeedSessionWithGuestLinkAsync()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"cross-cred-session-{Guid.NewGuid():N}@example.test");
        SessionDetails details = new(
            SessionTitle.Create("Cross-credential session"),
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
}
