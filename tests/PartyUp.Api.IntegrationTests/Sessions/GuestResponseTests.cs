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
/// D-017/D-126's guest claim/respond surface: a display name is the only thing a guest ever supplies,
/// and a second device's later write always wins under the same parent lock every registered mutation
/// takes — never a client-side version check.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GuestResponseTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ClaimRequiresOnlyADisplayName_D017()
    {
        (Session session, string token) = await SeedSessionWithGuestLinkAsync();
        (HttpClient client, string cookie, Guid guestLinkId) = await ExchangeAsync(token);
        using HttpClient _ = client;

        HttpResponseMessage claim = await AntiforgeryTestClient.SendAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/guest/{guestLinkId:N}/claim",
            JsonContent.Create(new { displayName = "Just A Name", response = "Pending" }),
            cookie);

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        Assert.Equal("Just A Name", body.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(session.Id.Value, body.RootElement.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task MultiDeviceLastWriteWinsUnderTheParentCapacityLock_D126()
    {
        (_, string token) = await SeedSessionWithGuestLinkAsync();
        (HttpClient client, string cookie, Guid guestLinkId) = await ExchangeAsync(token);
        using HttpClient ownedClient = client;

        HttpResponseMessage claim = await AntiforgeryTestClient.SendAsync(
            ownedClient,
            HttpMethod.Post,
            $"/api/v1/guest/{guestLinkId:N}/claim",
            JsonContent.Create(new { displayName = "Multi Device Guest", response = "Going" }),
            cookie);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        // Two "devices" share the same guest cookie/credential and respond in sequence; the second
        // write, from the second device, must win — with no version/conflict error surfaced.
        HttpResponseMessage deviceOne = await AntiforgeryTestClient.SendAsync(
            ownedClient,
            HttpMethod.Put,
            $"/api/v1/guest/{guestLinkId:N}/participation",
            JsonContent.Create(new { response = "Going" }),
            cookie);
        Assert.Equal(HttpStatusCode.OK, deviceOne.StatusCode);

        HttpResponseMessage deviceTwo = await AntiforgeryTestClient.SendAsync(
            ownedClient,
            HttpMethod.Put,
            $"/api/v1/guest/{guestLinkId:N}/participation",
            JsonContent.Create(new { response = "Maybe" }),
            cookie);
        Assert.Equal(HttpStatusCode.OK, deviceTwo.StatusCode);
        using JsonDocument finalBody = JsonDocument.Parse(await deviceTwo.Content.ReadAsStringAsync());
        Assert.Equal("maybe", finalBody.RootElement.GetProperty("response").GetString());

        var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/session");
        getRequest.Headers.Add(HeaderNames.Cookie, cookie);
        HttpResponseMessage read = await ownedClient.SendAsync(getRequest);
        using JsonDocument readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("maybe", readBody.RootElement.GetProperty("response").GetString());
    }

    private async Task<(Session Session, string Token)> SeedSessionWithGuestLinkAsync()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"guest-response-{Guid.NewGuid():N}@example.test");
        SessionDetails details = new(
            SessionTitle.Create("Guest response session"),
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

    private async Task<(HttpClient Client, string Cookie, Guid GuestLinkId)> ExchangeAsync(string token)
    {
        HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });
        HttpResponseMessage exchange = await client.GetAsync($"/g/{token}");
        SetCookieHeaderValue cookie = GuestExchangeTests.SetCookiesOf(exchange)
            .Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        Guid guestLinkId = Guid.Parse(cookie.Name.Value!["pu.g.".Length..]);
        return (client, GuestExchangeTests.CookieHeader(cookie), guestLinkId);
    }
}
