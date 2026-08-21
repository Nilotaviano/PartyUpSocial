using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// D-225 over HTTP: the guest cookie proves "I am that guest", the auth cookie proves "and this is my
/// account", and both are required. Refusals stay indistinguishable from a nonexistent link where the
/// caller has proven nothing, and the blocked-pair refusal is generic.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GuestUpgradeTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Upgrade_MovesTheClaimedSeatOntoTheAccountAndSpendsTheLink_D225()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        (SessionId sessionId, string token) = await SeedSessionAsync(organizer.Id);
        (string guestCookie, Guid guestLinkId) = await ExchangeAndClaimAsync(token, "Robin", "Going");

        SessionHttpUser account = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, account.Id);

        HttpResponseMessage upgrade = await AntiforgeryTestClient.PostAsync(
            session.Client,
            $"/api/v1/guest/{guestLinkId:N}/upgrade",
            content: null,
            $"{guestCookie}; {session.AuthCookie}");

        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(upgrade);
        Assert.Equal(HttpStatusCode.OK, upgrade.StatusCode);
        Assert.Equal(sessionId.Value, body.RootElement.GetProperty("sessionId").GetGuid());

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The roster now shows the account, in place: same Going response, no guest row left.
        Participation row = await context.SessionParticipations
            .SingleAsync(p => p.SessionId == sessionId && p.UserId == account.Id);
        Assert.Equal(ParticipationStatus.Going, row.Status);
        Assert.Equal(ParticipationOrigin.GuestLink, row.Origin);
        Assert.Empty(context.SessionParticipations
            .Where(p => p.SessionId == sessionId && p.GuestIdentityId != null));

        // The identity row survives as provenance, and the link is terminal.
        GuestIdentity identity = await context.GuestIdentities.SingleAsync(g => g.SessionId == sessionId);
        Assert.Equal(account.Id, identity.UpgradedToUserId);
        Assert.NotNull(identity.UpgradedAt);
        Assert.Equal("Robin", identity.DisplayName.Value);
        GuestLink link = await context.GuestLinks.SingleAsync(l => l.Id == GuestLinkId.From(guestLinkId));
        Assert.Equal(GuestLinkState.Upgraded, link.State);

        // Re-exchanging the token lands on the terminal notice — the seat belongs to the account now.
        using HttpClient bearer = ExchangeClient();
        HttpResponseMessage exchange = await bearer.GetAsync($"/g/{token}");
        Assert.Equal(HttpStatusCode.Found, exchange.StatusCode);
        SetCookieHeaderValue reissued = GuestExchangeTests.SetCookiesOf(exchange)
            .Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        using JsonDocument view = await GetGuestSessionAsync(
            bearer, guestLinkId, GuestExchangeTests.CookieHeader(reissued));
        Assert.Equal("revoked", view.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Upgrade_WhenTheAccountAlreadyHoldsARow_KeepsItAndFreesTheGuestSeat_D225()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser account = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, account.Id);
        (SessionId sessionId, string token) = await SeedSessionAsync(organizer.Id, invitee: account.Id);

        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, account.Id);
        HttpResponseMessage rsvp = await session.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/sessions/{sessionId.Value:D}/my-participation",
            System.Net.Http.Json.JsonContent.Create(new { response = "Maybe" }));
        Assert.True(rsvp.IsSuccessStatusCode, await rsvp.Content.ReadAsStringAsync());

        (string guestCookie, Guid guestLinkId) = await ExchangeAndClaimAsync(token, "Robin", "Going");

        HttpResponseMessage upgrade = await AntiforgeryTestClient.PostAsync(
            session.Client,
            $"/api/v1/guest/{guestLinkId:N}/upgrade",
            content: null,
            $"{guestCookie}; {session.AuthCookie}");
        Assert.Equal(HttpStatusCode.OK, upgrade.StatusCode);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The registered row is untouched; the guest row is Removed, so its slot is free again (D-044).
        Participation registered = await context.SessionParticipations
            .SingleAsync(p => p.SessionId == sessionId && p.UserId == account.Id);
        Assert.Equal(ParticipationStatus.Maybe, registered.Status);
        Assert.Equal(ParticipationOrigin.Invited, registered.Origin);

        Participation guestRow = await context.SessionParticipations
            .SingleAsync(p => p.SessionId == sessionId && p.GuestIdentityId != null);
        Assert.Equal(ParticipationStatus.Removed, guestRow.Status);
    }

    [Fact]
    public async Task Upgrade_WithoutAnAccountOrWithoutTheGuestCookie_IsRefusedFailClosed_D225()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        (_, string token) = await SeedSessionAsync(organizer.Id);
        (string guestCookie, Guid guestLinkId) = await ExchangeAndClaimAsync(token, "Robin", "Going");

        // Guest cookie but no account: the route's own policy answers 401 before anything else.
        using HttpClient anonymous = ExchangeClient();
        HttpResponseMessage unauthenticated = await AntiforgeryTestClient.PostAsync(
            anonymous,
            $"/api/v1/guest/{guestLinkId:N}/upgrade",
            content: null,
            guestCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        // Account but no guest cookie: fail closed as a 404, exactly like a nonexistent session.
        SessionHttpUser account = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, account.Id);
        HttpResponseMessage noCookie = await session.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/guest/{guestLinkId:N}/upgrade");
        await SessionEndpointTestSupport.AssertProblemAsync(
            noCookie, HttpStatusCode.NotFound, "session_not_found");
    }

    [Fact]
    public async Task Upgrade_ByABlockedPairOfTheOrganizer_Is409AndLeavesTheRosterUnchanged_D225()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser account = await SessionEndpointTestSupport.SeedAsync(_factory);
        (SessionId sessionId, string token) = await SeedSessionAsync(organizer.Id);
        (string guestCookie, Guid guestLinkId) = await ExchangeAndClaimAsync(token, "Robin", "Going");

        await using (AsyncServiceScope blockScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = blockScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Block block = Block.Create(
                organizer.Id, account.Id, Username.Create(account.Username), _factory.Clock);
            _ = block.DequeueEvents();
            context.Blocks.Add(block);
            await context.SaveChangesAsync();
        }

        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, account.Id);
        HttpResponseMessage upgrade = await AntiforgeryTestClient.PostAsync(
            session.Client,
            $"/api/v1/guest/{guestLinkId:N}/upgrade",
            content: null,
            $"{guestCookie}; {session.AuthCookie}");

        await SessionEndpointTestSupport.AssertProblemAsync(
            upgrade, HttpStatusCode.Conflict, "upgrade_unavailable");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext verify = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Participation guestRow = await verify.SessionParticipations
            .SingleAsync(p => p.SessionId == sessionId && p.GuestIdentityId != null);
        Assert.Equal(ParticipationStatus.Going, guestRow.Status);
        Assert.Empty(verify.SessionParticipations.Where(p => p.SessionId == sessionId && p.UserId == account.Id));
        GuestLink link = await verify.GuestLinks.SingleAsync(l => l.Id == GuestLinkId.From(guestLinkId));
        Assert.Equal(GuestLinkState.Active, link.State);
    }

    // ---- fixture helpers -------------------------------------------------------------------------

    private async Task<(SessionId SessionId, string Token)> SeedSessionAsync(UserId organizerId, UserId? invitee = null)
    {
        SessionDetails details = new(
            SessionTitle.Create("Guest upgrade session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromDays(1),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Private);
        Session session = Session.Create(
            organizerId, details, invitee is { } id ? [id] : [], _factory.Clock);
        (GuestLink _, string token) = session.CreateGuestLink(_factory.Clock);
        _ = session.DequeueEvents();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return (session.Id, token);
    }

    private async Task<(string Cookie, Guid GuestLinkId)> ExchangeAndClaimAsync(
        string token, string displayName, string response)
    {
        using HttpClient client = ExchangeClient();
        HttpResponseMessage exchange = await client.GetAsync($"/g/{token}");
        SetCookieHeaderValue cookie = GuestExchangeTests.SetCookiesOf(exchange)
            .Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        Guid guestLinkId = Guid.Parse(cookie.Name.Value!["pu.g.".Length..]);
        string header = GuestExchangeTests.CookieHeader(cookie);

        HttpResponseMessage claim = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/guest/{guestLinkId:N}/claim",
            System.Net.Http.Json.JsonContent.Create(new { displayName, response }),
            header);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        return (header, guestLinkId);
    }

    private static async Task<JsonDocument> GetGuestSessionAsync(HttpClient client, Guid guestLinkId, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/session");
        request.Headers.Add(HeaderNames.Cookie, cookie);
        HttpResponseMessage response = await client.SendAsync(request);
        return await SessionEndpointTestSupport.SuccessJsonAsync(response);
    }

    private HttpClient ExchangeClient() =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });
}
