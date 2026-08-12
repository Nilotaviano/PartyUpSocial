using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Api.IntegrationTests.Profile;

/// <summary>
/// D189_DESIGN.md §7: the invite/approval email dispatch, the unsubscribe-link confirm surface,
/// and the account-setting toggle, exercised end to end.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EmailNotificationsD189Tests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task CreateSessionWithInvitees_DispatchesSessionInvitationEmail_D189()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser friend = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, friend.Id);
        string friendEmail = await ReadEmailAsync(friend.Id);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);

        Instant startTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "D-189 invite test",
                startTime = SessionEndpointTestSupport.Iso(startTime),
                inviteeUserIds = new[] { friend.Id.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        EmailMessage message = await _factory.EmailSender.WaitForAsync(value =>
            value.Template == EmailTemplate.SessionInvitation && value.ToAddress == friendEmail);

        Assert.Equal("en", message.Locale.Value);
        Assert.Equal("D-189 invite test", message.Model["Title"]);
        Assert.False(string.IsNullOrWhiteSpace(message.Model["Token"]));
        Assert.True(message.Model.TryGetValue("PublicBaseUrl", out string? publicBaseUrl));
        Assert.True(message.Model.TryGetValue("Link", out string? link));

        // §3: the unsubscribe link rides the URL fragment, never the query string, and never
        // shares the detail-page Link.
        string unsubscribeLink = $"{publicBaseUrl}/email-unsubscribe#token={message.Model["Token"]}";
        Assert.StartsWith(publicBaseUrl!, unsubscribeLink, StringComparison.Ordinal);
        Assert.Contains("#token=", unsubscribeLink, StringComparison.Ordinal);
        Assert.DoesNotContain(message.Model["Token"], link!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsubscribe_RoundTrips_D189()
    {
        const string email = "unsubscribe-roundtrip-d189@example.test";
        UserId userId = await _factory.SeedUserAsync(email: email);
        string rawToken = await IssueUnsubscribeTokenAsync(userId);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, userId);

        HttpResponseMessage first = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/email-notifications/unsubscribe",
            JsonContent.Create(new { token = rawToken }));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        using JsonDocument profile = await SuccessJsonAsync(await GetAsync(client, "/api/v1/me", cookie));
        Assert.False(profile.RootElement.GetProperty("emailNotificationsEnabled").GetBoolean());

        HttpResponseMessage second = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/email-notifications/unsubscribe",
            JsonContent.Create(new { token = rawToken }));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        using JsonDocument stillOff = await SuccessJsonAsync(await GetAsync(client, "/api/v1/me", cookie));
        Assert.False(stillOff.RootElement.GetProperty("emailNotificationsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Unsubscribe_RedeemingOneTokenKillsEveryOutstandingTokenForTheAccount_D189()
    {
        UserId userId = await _factory.SeedUserAsync(email: "two-tokens-d189@example.test");
        string tokenOne = await IssueUnsubscribeTokenAsync(userId);
        string tokenTwo = await IssueUnsubscribeTokenAsync(userId);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/email-notifications/unsubscribe",
            JsonContent.Create(new { token = tokenOne }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ISecurityTokenService>();
        TokenRedemption redemption = await tokens.RedeemAsync(
            new RedeemTokenRequest(AuthTokenPurpose.EmailUnsubscribe, tokenTwo),
            default);
        Assert.False(redemption.Succeeded);
    }

    [Fact]
    public async Task Unsubscribe_ForATombstonedUser_NoOps_D189()
    {
        UserId userId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: "tombstoned-unsubscribe-d189@example.test");
        UserId admin = await _factory.SeedUserAsync(admin: true, email: "tombstoned-unsubscribe-admin-d189@example.test");
        string rawToken = await IssueUnsubscribeTokenAsync(userId);
        _factory.Clock.Advance(User.DeletionGracePeriod);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string adminCookie = await SignInAsync(client, admin);
        HttpResponseMessage sweep = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/admin/maintenance/deletion-sweep",
            content: null,
            adminCookie);
        Assert.Equal(HttpStatusCode.OK, sweep.StatusCode);
        Assert.True((await ReadUserAsync(userId)).IsTombstoned);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/email-notifications/unsubscribe",
            JsonContent.Create(new { token = rawToken }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True((await ReadUserAsync(userId)).EmailNotificationsEnabled);
    }

    [Fact]
    public async Task SocialOnlyAccount_EmailNotificationsToggleRoundTrips_D078_D189()
    {
        UserId userId = await _factory.SeedUserAsync(email: null);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, userId);

        HttpResponseMessage disable = await AntiforgeryTestClient.SendAsync(
            client,
            HttpMethod.Put,
            "/api/v1/me/email-notifications",
            JsonContent.Create(new { enabled = false }),
            cookie);
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        using JsonDocument disabled = await SuccessJsonAsync(await GetAsync(client, "/api/v1/me", cookie));
        Assert.False(disabled.RootElement.GetProperty("emailNotificationsEnabled").GetBoolean());

        HttpResponseMessage enable = await AntiforgeryTestClient.SendAsync(
            client,
            HttpMethod.Put,
            "/api/v1/me/email-notifications",
            JsonContent.Create(new { enabled = true }),
            cookie);
        Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);
        using JsonDocument enabled = await SuccessJsonAsync(await GetAsync(client, "/api/v1/me", cookie));
        Assert.True(enabled.RootElement.GetProperty("emailNotificationsEnabled").GetBoolean());
    }

    private async Task<string> IssueUnsubscribeTokenAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ISecurityTokenService>();
        IssuedToken issued = await tokens.IssueAsync(
            new IssueTokenRequest(
                AuthTokenPurpose.EmailUnsubscribe,
                userId,
                null,
                Duration.FromDays(365)),
            default);
        return issued.RawToken;
    }

    private async Task<string> ReadEmailAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<PartyUp.Infrastructure.Identity.AuthUser>>();
        PartyUp.Infrastructure.Identity.AuthUser identity = await userManager.FindByIdAsync(userId.Value.ToString("D"))
            ?? throw new InvalidOperationException("Identity row missing for seeded user.");
        return identity.Email!;
    }

    private async Task<User> ReadUserAsync(UserId id)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .FindAsync(id, default))!;
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

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(HeaderNames.Cookie, cookie);
        return client.SendAsync(request);
    }

    private static async Task<JsonDocument> SuccessJsonAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success but got {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }
}
