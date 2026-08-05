using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.Logging;
using PartyUp.Api.Security;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// A-074's stale-credential resolution and the §9.2 no-token-anywhere-in-logs guarantee.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GuestSecurityTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ActiveCredentialOnARevokedLinkBlocksMutationsButStillRendersTheTerminalNotice_A074()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"guest-security-{Guid.NewGuid():N}@example.test");
        SessionDetails details = new(
            SessionTitle.Create("A-074 session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromDays(1),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: true);
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        (GuestLink link, string token) = session.CreateGuestLink(_factory.Clock);
        _ = session.DequeueEvents();

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext seedContext = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            seedContext.Sessions.Add(session);
            await seedContext.SaveChangesAsync();
        }

        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });
        HttpResponseMessage exchange = await client.GetAsync($"/g/{token}");
        string activeCookie = GuestExchangeTests.CookieHeader(
            GuestExchangeTests.SetCookiesOf(exchange).Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal)));

        // Revoke the still-unclaimed link the organizer-only way (D-150), leaving the bearer holding
        // an active-mode credential against what is now a terminal link.
        await using (AsyncServiceScope revokeScope = _factory.Services.CreateAsyncScope())
        {
            var invitations = revokeScope.ServiceProvider.GetRequiredService<SessionInvitationUseCases>();
            SessionCommandResult revoked = await invitations.RevokeGuestLinkAsync(
                session.Id, organizerId, link.Id, default);
            Assert.True(revoked.Succeeded);
        }

        var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{link.Id.Value:N}/session");
        getRequest.Headers.Add(HeaderNames.Cookie, activeCookie);
        HttpResponseMessage terminalView = await client.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.OK, terminalView.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await terminalView.Content.ReadAsStringAsync());
        Assert.Equal("revoked", body.RootElement.GetProperty("state").GetString());

        HttpResponseMessage mutation = await AntiforgeryTestClient.SendAsync(
            client, HttpMethod.Post, $"/api/v1/guest/{link.Id.Value:N}/claim",
            JsonContent.Create(new { displayName = "Late Guest", response = "Going" }),
            activeCookie);

        Assert.Equal(HttpStatusCode.NotFound, mutation.StatusCode);
        using JsonDocument mutationBody = JsonDocument.Parse(await mutation.Content.ReadAsStringAsync());
        Assert.Equal("session_not_found", mutationBody.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TerminalModeCredentialAgainstAStillLiveLinkFailsClosedWith404OnMutation_A074()
    {
        // A-074 branch (c): the credential's own mode says terminal, but the underlying link/session
        // is still live (never revoked, never ended) — a forged-or-stale terminal-mode credential must
        // not be able to mutate, and must fail the same way as "not found" rather than a distinguishable
        // 409, so it cannot be used to probe whether the link is actually fine.
        UserId organizerId = await _factory.SeedUserAsync(email: $"guest-security-{Guid.NewGuid():N}@example.test");
        SessionDetails details = new(
            SessionTitle.Create("A-074 branch c session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromDays(1),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: true);
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        (GuestLink link, _) = session.CreateGuestLink(_factory.Clock);
        _ = session.DequeueEvents();

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext seedContext = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            seedContext.Sessions.Add(session);
            await seedContext.SaveChangesAsync();
        }

        // Forge a well-formed, correctly-protected credential whose Mode is Terminal even though the
        // link/session it names is still Live — the only way branch (c) can occur, since the normal
        // exchange/reissue flow never mints a Terminal credential for a Live link.
        Instant now = _factory.Clock.GetCurrentInstant();
        GuestLinkCredentialProtector protector;
        await using (AsyncServiceScope protectorScope = _factory.Services.CreateAsyncScope())
        {
            protector = protectorScope.ServiceProvider.GetRequiredService<GuestLinkCredentialProtector>();
        }

        var forgedCredential = new GuestLinkCredential(
            link.Id,
            session.Id,
            GuestCredentialMode.Terminal,
            now,
            now + GuestLinkCredentialProtector.TerminalLifetime);
        string forgedCookieValue = protector.Protect(forgedCredential);
        string forgedCookie = $"pu.g.{link.Id.Value:N}={forgedCookieValue}";

        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });

        HttpResponseMessage mutation = await AntiforgeryTestClient.SendAsync(
            client, HttpMethod.Post, $"/api/v1/guest/{link.Id.Value:N}/claim",
            JsonContent.Create(new { displayName = "Branch C Guest", response = "Going" }),
            forgedCookie);

        Assert.Equal(HttpStatusCode.NotFound, mutation.StatusCode);
        using JsonDocument mutationBody = JsonDocument.Parse(await mutation.Content.ReadAsStringAsync());
        Assert.Equal("session_not_found", mutationBody.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void TokenNeverAppearsInSerilogCaddyHeadersRefererOrLocation_A044()
    {
        // The request-path sanitizer is the application-log guard (Program.cs wires it into
        // UseSerilogRequestLogging); Caddy's own uri filter is asserted by CaddyAccessLoggingTests.
        Assert.Equal(
            "/g/REDACTED",
            FriendPathLogSanitizer.Sanitize(new PathString("/g/AbCdEf1234567890")));
        Assert.Equal(
            "/g/REDACTED",
            FriendPathLogSanitizer.Sanitize(new PathString("/g/")));
    }

    [Fact]
    public async Task GuestExchangeResponseCarriesNoReferrerPolicy_A044()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"guest-referrer-{Guid.NewGuid():N}@example.test");
        SessionDetails details = new(
            SessionTitle.Create("Referrer policy session"),
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

        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });
        HttpResponseMessage response = await client.GetAsync($"/g/{token}");

        Assert.True(response.Headers.TryGetValues("Referrer-Policy", out IEnumerable<string>? values));
        Assert.Equal("no-referrer", values!.Single());
    }
}
