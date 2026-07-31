using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SecurityStampTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Response_ForAValidSession_CarriesNoValidatorIssuedSetCookie()
    {
        UserId userId = await _factory.SeedUserAsync();
        using HttpClient client = CreateClient();
        string cookie = await SignInAsync(client, userId);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/_slice2/authenticated");
        request.Headers.Add(HeaderNames.Cookie, cookie);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
    }

    [Fact]
    public async Task Response_ForAPrincipalWithNoProjectionRow_IsRejected()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var identityOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<IdentityOptions>>()
            .Value;
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme);
        identity.AddClaim(new Claim(
            identityOptions.ClaimsIdentity.UserIdClaimType,
            Guid.NewGuid().ToString("D")));
        identity.AddClaim(new Claim(
            identityOptions.ClaimsIdentity.SecurityStampClaimType,
            Guid.NewGuid().ToString("N")));
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
            },
            IdentityConstants.ApplicationScheme);
        string protectedTicket = cookieOptions.TicketDataFormat.Protect(ticket);
        using HttpClient client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/_slice2/authenticated");
        request.Headers.Add(HeaderNames.Cookie, $"pu.auth={protectedTicket}");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues(HeaderNames.SetCookie),
            value => value.StartsWith("pu.auth=;", StringComparison.Ordinal));
    }

    private HttpClient CreateClient() => _factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await client.PostAsync(
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        return response.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }
}
