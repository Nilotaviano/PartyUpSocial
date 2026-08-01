using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Auth;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ExternalRedirectTests : IAsyncLifetime, IDisposable
{
    private const string Password = "external-flow-password";
    private readonly PartyUpApiFactory _factory = new(googleEnabled: true);

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/app")]
    [InlineData("/onboarding")]
    [InlineData("/settings/profile")]
    [InlineData("/settings/account")]
    public void ReturnToValidator_RetainsEveryFixedPath(string path)
    {
        Assert.Equal(path, ReturnToValidator.Sanitize(path));
    }

    [Theory]
    [InlineData("/u/Player.Name", "/u/player.name")]
    [InlineData("/add/2345-6789-AB", "/add/23456789AB")]
    public void ReturnToValidator_CanonicalizesStrictFriendRoutes(
        string requested,
        string expected)
    {
        Assert.Equal(expected, ReturnToValidator.Sanitize(requested));
    }

    [Theory]
    [InlineData("//evil.test/u/player")]
    [InlineData("/u/player/extra")]
    [InlineData("/u/player%2Fextra")]
    [InlineData("/add/23456789AB?next=/app")]
    public void ReturnToValidator_DropsHostileFriendRoutes(string requested)
    {
        Assert.Equal("/app", ReturnToValidator.Sanitize(requested));
    }

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

    [Theory]
    [InlineData("/u/Player.Name", "/u/player.name", true)]
    [InlineData("/add/2345-6789-AB", "/add/23456789AB", true)]
    [InlineData("/u/Player.Name", "/u/player.name", false)]
    [InlineData("/add/2345-6789-AB", "/add/23456789AB", false)]
    public async Task GoogleCallback_PreservesCanonicalFriendContinuationForEveryAccountState(
        string requested,
        string expected,
        bool completeProfile)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string providerKey = $"continuation-{suffix}";
        string email = $"continuation-{suffix}@example.test";
        UserId userId = await _factory.SeedUserAsync(
            completeProfile: completeProfile,
            email: email);
        await AddGoogleLoginAsync(userId, providerKey);

        ExternalCompletion completion = await CompleteGoogleAsync(
            providerKey,
            "login",
            email,
            returnTo: requested);

        string next = QueryHelpers.ParseQuery(
            new Uri(new Uri("https://localhost"), completion.Response.Headers.Location!).Query)
            ["next"]!;
        Assert.Equal(expected, next);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("link")]
    [InlineData("reauth")]
    [InlineData("refusal")]
    [InlineData("deletion")]
    public async Task NoRedirectLocationFromAnExternalLegContainsARawTokenInItsQuery(
        string completionKind)
    {
        ExternalCompletion completion = await CompleteAsync(completionKind);

        string location = completion.Response.Headers.Location!.OriginalString;
        string query = location.Split('#', 2)[0];
        Assert.DoesNotContain("reauth=", query, StringComparison.Ordinal);
        Assert.DoesNotContain("intent=", query, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", query, StringComparison.Ordinal);
        if (completionKind == "reauth")
        {
            Assert.Contains("#reauth=", location, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("#reauth=", location, StringComparison.Ordinal);
        }

        if (completionKind == "deletion")
        {
            Assert.Contains("#token=", location, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("login")]
    [InlineData("link")]
    [InlineData("reauth")]
    [InlineData("refusal")]
    [InlineData("deletion")]
    public async Task EveryCompletionLandsOnTheHashClearingCallbackRoute(string completionKind)
    {
        ExternalCompletion completion = await CompleteAsync(completionKind);

        string location = completion.Response.Headers.Location!.OriginalString;
        string expected = completionKind == "deletion"
            ? "/account-deletion/confirm#token="
            : "/auth/callback?";
        Assert.StartsWith(expected, location, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Complete_WithoutEmailVerifiedClaimRefusesBindingOrCreation_D067(
        bool matchingAccountExists)
    {
        const string email = "absent-verified-claim@example.test";
        UserId? existingUserId = matchingAccountExists
            ? await _factory.SeedUserAsync(email: email)
            : null;

        ExternalCompletion completion = await CompleteGoogleAsync(
            "absent-verified-claim-key",
            "login",
            email,
            includeEmailVerifiedClaim: false);

        Assert.Contains(
            "error=link_required",
            completion.Response.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        AuthUser? account = await users.FindByEmailAsync(email);
        if (existingUserId is { } expectedId)
        {
            Assert.NotNull(account);
            Assert.Equal(expectedId.Value, account.Id);
            Assert.Empty(await users.GetLoginsAsync(account));
        }
        else
        {
            Assert.Null(account);
        }
    }

    [Fact]
    public async Task Complete_WithALinkFlowMissingItsBoundUser_LinksNothingAtHttpLayer_D087()
    {
        const string email = "missing-bound-user@example.test";
        UserId userId = await _factory.SeedUserAsync(email: email, password: Password);

        ExternalCompletion completion = await CompleteGoogleAsync(
            "missing-bound-user-key",
            "link",
            email,
            userId,
            Password,
            mutateFlow: properties => properties.Items.Remove("partyup.boundUserId"));

        Assert.Contains(
            "error=intent_mode_mismatch",
            completion.Response.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        Assert.Equal(0, await LoginCountAsync(userId));
    }

    private async Task<ExternalCompletion> CompleteAsync(string completionKind)
    {
        string providerKey = $"{completionKind}-completion-key";
        string email = $"{completionKind}-completion@example.test";

        switch (completionKind)
        {
            case "login":
            {
                UserId userId = await _factory.SeedUserAsync(email: email);
                await AddGoogleLoginAsync(userId, providerKey);
                return await CompleteGoogleAsync(providerKey, "login", email);
            }
            case "link":
            {
                UserId userId = await _factory.SeedUserAsync(email: email, password: Password);
                return await CompleteGoogleAsync(
                    providerKey,
                    "link",
                    email,
                    userId,
                    Password);
            }
            case "reauth":
            {
                UserId userId = await _factory.SeedUserAsync(email: email);
                await AddGoogleLoginAsync(userId, providerKey);
                return await CompleteGoogleAsync(
                    providerKey,
                    "reauth",
                    email,
                    userId);
            }
            case "refusal":
                return await CompleteGoogleAsync(
                    providerKey,
                    "login",
                    email,
                    includeEmailVerifiedClaim: false);
            case "deletion":
                return await CompleteDeletionAsync();
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(completionKind),
                    completionKind,
                    "Unknown completion kind.");
        }
    }

    private async Task<ExternalCompletion> CompleteDeletionAsync()
    {
        const string steamId = "76561198000000000";
        UserId userId = await _factory.SeedUserAsync(email: null);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAccountGateway>()
                .AddLoginAsync(
                    userId,
                    ExternalProviderPolicy.Steam,
                    steamId,
                    default);
        }

        var verifier = new AcceptingSteamVerifier(steamId);
        await using WebApplicationFactory<Program> deletionFactory =
            _factory.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ISteamOpenIdVerifier>();
                    services.AddSingleton<ISteamOpenIdVerifier>(verifier);
                }));
        using HttpClient client = Slice5HttpTest.CreateClient(deletionFactory);
        HttpResponseMessage start = await client.GetAsync(
            "/api/v1/account-deletion-requests/steam/start");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        string stateCookie = Cookie(
            start,
            cookie => cookie.Name.Value == "pu.extdel");
        string returnTo = QueryHelpers.ParseQuery(start.Headers.Location!.Query)
            ["openid.return_to"]!;
        string nonce = QueryHelpers.ParseQuery(new Uri(returnTo).Query)["n"]!;
        using var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/account-deletion-requests/steam/callback?n={Uri.EscapeDataString(nonce)}");
        callback.Headers.Add(HeaderNames.Cookie, stateCookie);

        HttpResponseMessage response = await client.SendAsync(callback);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, verifier.Calls);
        return new ExternalCompletion(response);
    }

    private async Task<ExternalCompletion> CompleteGoogleAsync(
        string providerKey,
        string mode,
        string email,
        UserId? authenticatedUserId = null,
        string? currentPassword = null,
        bool includeEmailVerifiedClaim = true,
        Action<AuthenticationProperties>? mutateFlow = null,
        string returnTo = "/settings/account")
    {
        await using WebApplicationFactory<Program> googleFactory =
            CreateGoogleFactory(
                providerKey,
                email,
                includeEmailVerifiedClaim,
                mutateFlow);
        using HttpClient client = Slice5HttpTest.CreateClient(googleFactory);
        string? authCookie = authenticatedUserId is { } userId
            ? await SignInAsync(client, userId)
            : null;
        MintedIntent minted = await MintAsync(
            client,
            mode,
            authCookie,
            currentPassword,
            returnTo);
        HttpResponseMessage challenge = await StartAsync(client, minted, authCookie);
        string externalCookie = Cookie(
            challenge,
            cookie => cookie.Name.Value?.Contains(
                "Identity.External",
                StringComparison.Ordinal) == true);

        using var complete = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/auth/external/complete");
        complete.Headers.Add(
            HeaderNames.Cookie,
            authCookie is null ? externalCookie : $"{authCookie}; {externalCookie}");
        HttpResponseMessage response = await client.SendAsync(complete);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return new ExternalCompletion(response);
    }

    private WebApplicationFactory<Program> CreateGoogleFactory(
        string providerKey,
        string email,
        bool includeEmailVerifiedClaim = true,
        Action<AuthenticationProperties>? mutateFlow = null) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.PostConfigure<GoogleOptions>(
                    GoogleDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.Events.OnRedirectToAuthorizationEndpoint = async context =>
                        {
                            var claims = new List<Claim>
                            {
                                new(ClaimTypes.NameIdentifier, providerKey),
                                new(ClaimTypes.Email, email),
                            };
                            if (includeEmailVerifiedClaim)
                            {
                                claims.Add(new Claim("email_verified", "true"));
                            }

                            mutateFlow?.Invoke(context.Properties);
                            var identity = new ClaimsIdentity(
                                claims,
                                GoogleDefaults.AuthenticationScheme);
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

    private async Task<int> LoginCountAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        AuthUser user = (await users.FindByIdAsync(userId.Value.ToString("D")))!;
        return (await users.GetLoginsAsync(user)).Count;
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
        string? authCookie = null,
        string? currentPassword = null,
        string returnTo = "/settings/account")
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/external/intent",
            JsonContent.Create(new
            {
                provider = "google",
                mode,
                returnTo,
                ageGateAttested = true,
                currentPassword,
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

    private sealed class AcceptingSteamVerifier(string steamId) : ISteamOpenIdVerifier
    {
        public int Calls { get; private set; }

        public Task<SteamOpenIdVerification> VerifyAsync(
            IReadOnlyDictionary<string, string> assertion,
            string expectedReturnTo,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SteamOpenIdVerification(true, steamId));
        }
    }

    private sealed record MintedIntent(string Intent, string BinderCookie);

    private sealed record ExternalCompletion(HttpResponseMessage Response);
}
