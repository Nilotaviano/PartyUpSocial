using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ExternalRedirectTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new(googleEnabled: true);

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GoogleChallenge_SetsTheCorrelationCookieSameSiteLax()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        MintedIntent minted = await MintAsync(client, "login");

        HttpResponseMessage challenge = await StartAsync(client, minted);

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        SetCookieHeaderValue correlation = challenge.Headers
            .GetValues(HeaderNames.SetCookie)
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(cookie => cookie.Name.Value?.Contains(
                "Correlation",
                StringComparison.Ordinal) == true);
        Assert.Equal(SameSiteMode.Lax, correlation.SameSite);
        Assert.DoesNotContain(
            challenge.Headers.GetValues(HeaderNames.SetCookie),
            value => value.Contains("SameSite=None", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoRedirectLocationFromAnExternalLegContainsARawTokenInItsQuery()
    {
        ExternalCompletion completion = await CompleteReauthAsync("raw-token-query-check");

        string location = completion.Response.Headers.Location!.OriginalString;
        string query = location.Split('#', 2)[0];
        Assert.DoesNotContain("reauth=", query, StringComparison.Ordinal);
        Assert.Contains("#reauth=", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCompletionLandsOnTheHashClearingCallbackRoute()
    {
        ExternalCompletion completion = await CompleteReauthAsync("callback-route-check");

        string location = completion.Response.Headers.Location!.OriginalString;
        Assert.StartsWith("/auth/callback?", location, StringComparison.Ordinal);
    }

    private async Task<ExternalCompletion> CompleteReauthAsync(string providerKey)
    {
        UserId userId = await _factory.SeedUserAsync();
        await AddGoogleLoginAsync(userId, providerKey);
        await using WebApplicationFactory<Program> googleFactory =
            CreateGoogleFactory(providerKey);
        using HttpClient client = Slice5HttpTest.CreateClient(googleFactory);
        string authCookie = await SignInAsync(client, userId);
        MintedIntent minted = await MintAsync(client, "reauth", authCookie);
        HttpResponseMessage challenge = await StartAsync(client, minted, authCookie);
        string externalCookie = Cookie(
            challenge,
            cookie => cookie.Name.Value?.Contains(
                "Identity.External",
                StringComparison.Ordinal) == true);

        using var complete = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/auth/external/complete");
        complete.Headers.Add(HeaderNames.Cookie, $"{authCookie}; {externalCookie}");
        HttpResponseMessage response = await client.SendAsync(complete);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return new ExternalCompletion(response);
    }

    private WebApplicationFactory<Program> CreateGoogleFactory(string providerKey) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.PostConfigure<GoogleOptions>(
                    GoogleDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.Events.OnRedirectToAuthorizationEndpoint = async context =>
                        {
                            var identity = new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, providerKey),
                                new Claim(ClaimTypes.Email, "provider@example.test"),
                                new Claim("email_verified", "true"),
                            ], GoogleDefaults.AuthenticationScheme);
                            await context.HttpContext.SignInAsync(
                                IdentityConstants.ExternalScheme,
                                new ClaimsPrincipal(identity),
                                context.Properties);
                            context.Response.Redirect(context.Properties.RedirectUri!);
                        };
                    })));

    private async Task AddGoogleLoginAsync(UserId userId, string providerKey)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        AuthUser user = (await users.FindByIdAsync(userId.Value.ToString("D")))!;
        Assert.True((await users.AddLoginAsync(
            user,
            new UserLoginInfo("google", providerKey, "google"))).Succeeded);
    }

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Cookie(response, cookie => cookie.Name.Value == "pu.auth");
    }

    private static async Task<MintedIntent> MintAsync(
        HttpClient client,
        string mode,
        string? authCookie = null)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/external/intent",
            JsonContent.Create(new
            {
                provider = "google",
                mode,
                returnTo = "/settings/account",
                ageGateAttested = true,
            }),
            authCookie);
        response.EnsureSuccessStatusCode();
        ExternalIntentResponse body =
            (await response.Content.ReadFromJsonAsync<ExternalIntentResponse>())!;
        return new MintedIntent(
            body.Intent,
            Cookie(response, cookie => cookie.Name.Value == "pu.intent"));
    }

    private static async Task<HttpResponseMessage> StartAsync(
        HttpClient client,
        MintedIntent minted,
        string? authCookie = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/auth/external/google/start?intent={Uri.EscapeDataString(minted.Intent)}");
        request.Headers.Add(
            HeaderNames.Cookie,
            authCookie is null ? minted.BinderCookie : $"{authCookie}; {minted.BinderCookie}");
        return await client.SendAsync(request);
    }

    private static string Cookie(
        HttpResponseMessage response,
        Func<SetCookieHeaderValue, bool> predicate)
    {
        SetCookieHeaderValue cookie = response.Headers
            .GetValues(HeaderNames.SetCookie)
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(predicate);
        return $"{cookie.Name.Value}={cookie.Value.Value}";
    }

    private sealed record MintedIntent(string Intent, string BinderCookie);

    private sealed record ExternalCompletion(HttpResponseMessage Response);
}
