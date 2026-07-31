using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using PartyUp.Api.Auth;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.Endpoints;

public static class ExternalAuthEndpoints
{
    private const string IntentCookieName = "pu.intent";
    private const string IntentProtectorPurpose = "PartyUp.IntentBinder";
    private const string IntentCookiePath = "/api/v1/auth/external";
    private const int MaximumBinders = 5;
    private const string SteamStateCookieName = "pu.ext";
    private const string SteamStateCookiePath = "/api/v1/auth/external/steam";
    private const string SteamStateProtectorPurpose = "PartyUp.SteamState.Auth";

    private const string PropertyProvider = "partyup.provider";
    private const string PropertyMode = "partyup.mode";
    private const string PropertyReturnTo = "partyup.returnTo";
    private const string PropertyAgeGate = "partyup.ageGateAttested";
    private const string PropertyBoundUserId = "partyup.boundUserId";

    public static RouteGroupBuilder MapExternalAuthEndpoints(
        this RouteGroupBuilder api,
        bool googleEnabled,
        bool steamEnabled,
        IHostEnvironment environment)
    {
        RouteGroupBuilder external = api.MapGroup("/auth/external");

        external.MapPost("/intent", IssueIntentAsync)
            .WithName(ApiOperationIds.IssueExternalIntent)
            .Produces<ExternalIntentResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

        if (googleEnabled)
        {
            external.MapGet("/google/start", (
                    string? intent,
                    HttpContext context,
                    IDataProtectionProvider dataProtection,
                    ISecurityTokenService tokens,
                    CancellationToken cancellationToken) =>
                    StartGoogleAsync(
                        intent,
                        context,
                        dataProtection,
                        tokens,
                        environment,
                        cancellationToken))
                .WithName("StartGoogleExternal")
                .Produces(StatusCodes.Status302Found)
                .ProducesProblem(StatusCodes.Status400BadRequest);

            external.MapGet("/complete", CompleteAsync)
                .WithName("CompleteExternalAuthentication")
                .Produces(StatusCodes.Status302Found);
        }

        if (steamEnabled)
        {
            external.MapGet("/steam/start", StartSteamAsync)
                .WithName("StartSteamExternal")
                .Produces(StatusCodes.Status302Found)
                .ProducesProblem(StatusCodes.Status400BadRequest);
            external.MapGet("/steam/callback", CompleteSteamAsync)
                .WithName("CompleteSteamExternal")
                .Produces(StatusCodes.Status302Found)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized);
        }

        return api;
    }

    private static async Task<IResult> IssueIntentAsync(
        ExternalIntentRequest request,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        IssueExternalIntentUseCase useCase,
        ProviderCapabilities providers,
        IDataProtectionProvider dataProtection,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(request.Mode, ignoreCase: true, out ExternalStartMode mode))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_request");
        }

        string provider = request.Provider?.Trim() ?? string.Empty;
        if (!ExternalProviderPolicy.IsSupported(provider) ||
            !providers.Values.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            return Problem(context, StatusCodes.Status409Conflict, "provider_unavailable");
        }

        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        byte[] binder = IntentBinder.Create();
        ExternalIntentIssueResult result = await useCase.ExecuteAsync(
            new IssueExternalIntentCommand(
                provider,
                mode,
                ReturnToValidator.Sanitize(request.ReturnTo),
                request.AgeGateAttested == true,
                IntentBinder.Hash(binder),
                caller?.Id,
                request.CurrentPassword,
                request.ReauthToken),
            cancellationToken);

        if (!result.Succeeded)
        {
            int status = result.Code == "unauthorized"
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status409Conflict;
            return Problem(context, status, result.Code!);
        }

        IDataProtector protector = dataProtection.CreateProtector(IntentProtectorPurpose);
        List<string> binders = ReadBinders(context.Request.Cookies[IntentCookieName], protector);
        binders.Add(IntentBinder.Encode(binder));
        if (binders.Count > MaximumBinders)
        {
            binders.RemoveRange(0, binders.Count - MaximumBinders);
        }

        WriteBinders(context.Response, binders, protector, environment);
        return Results.Ok(new ExternalIntentResponse(result.Intent!));
    }

    private static async Task<IResult> StartGoogleAsync(
        string? intent,
        HttpContext context,
        IDataProtectionProvider dataProtection,
        ISecurityTokenService tokens,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        IntentRedemption? started = await RedeemIntentAsync(
            ExternalProviderPolicy.Google,
            intent,
            context,
            dataProtection,
            tokens,
            environment,
            cancellationToken);
        if (started is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "intent_required");
        }

        TokenRedemption redemption = started.Redemption;
        ExternalIntentPayload payload = started.Payload;

        var properties = new AuthenticationProperties
        {
            RedirectUri = $"{ApiRoutes.V1}/auth/external/complete",
        };
        properties.Items[PropertyProvider] = payload.Provider;
        properties.Items[PropertyMode] = payload.Mode.ToString();
        properties.Items[PropertyReturnTo] = payload.ReturnTo;
        properties.Items[PropertyAgeGate] = payload.AgeGateAttested ? "true" : "false";
        if (redemption.UserId is { } boundUserId)
        {
            properties.Items[PropertyBoundUserId] = boundUserId.Value.ToString("D");
        }

        return Results.Challenge(properties, [GoogleDefaults.AuthenticationScheme]);
    }

    private static async Task<IntentRedemption?> RedeemIntentAsync(
        string provider,
        string? intent,
        HttpContext context,
        IDataProtectionProvider dataProtection,
        ISecurityTokenService tokens,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            return null;
        }

        IDataProtector protector = dataProtection.CreateProtector(IntentProtectorPurpose);
        List<string> binders = ReadBinders(context.Request.Cookies[IntentCookieName], protector);
        for (int index = 0; index < binders.Count; index++)
        {
            if (!IntentBinder.TryDecode(binders[index], out byte[] binder))
            {
                continue;
            }

            TokenRedemption redemption = await tokens.RedeemAsync(
                new RedeemTokenRequest(
                    AuthTokenPurpose.ExternalStartIntent,
                    intent,
                    BinderHash: IntentBinder.Hash(binder)),
                cancellationToken);
            if (!redemption.Succeeded ||
                redemption.Intent is not { } payload ||
                !string.Equals(payload.Provider, provider, StringComparison.Ordinal))
            {
                continue;
            }

            binders.RemoveAt(index);
            WriteBinders(context.Response, binders, protector, environment);
            return new IntentRedemption(redemption, payload);
        }

        return null;
    }

    private static async Task<IResult> StartSteamAsync(
        string? intent,
        HttpContext context,
        IDataProtectionProvider dataProtection,
        ISecurityTokenService tokens,
        IConfiguration configuration,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        IntentRedemption? started = await RedeemIntentAsync(
            ExternalProviderPolicy.Steam,
            intent,
            context,
            dataProtection,
            tokens,
            environment,
            cancellationToken);
        if (started is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "intent_required");
        }

        string nonce = IntentBinder.Encode(IntentBinder.Create());
        string callback = SteamCallbackUrl(configuration, nonce);
        var state = new SteamAuthState(
            "auth",
            nonce,
            started.Payload.Mode,
            started.Payload.ReturnTo,
            started.Redemption.UserId?.Value,
            started.Payload.AgeGateAttested);
        IDataProtector protector = dataProtection.CreateProtector(SteamStateProtectorPurpose);
        context.Response.Cookies.Append(
            SteamStateCookieName,
            protector.Protect(JsonSerializer.Serialize(state)),
            SteamStateCookieOptions(environment));

        string realm = SteamRealm(configuration);
        var parameters = new Dictionary<string, string?>
        {
            ["openid.ns"] = SteamOpenIdVerifier.OpenIdNamespace,
            ["openid.mode"] = "checkid_setup",
            ["openid.identity"] = SteamOpenIdVerifier.OpenIdNamespace + "/identifier_select",
            ["openid.claimed_id"] = SteamOpenIdVerifier.OpenIdNamespace + "/identifier_select",
            ["openid.return_to"] = callback,
            ["openid.realm"] = realm,
        };
        return Results.Redirect(QueryHelpers.AddQueryString(
            SteamOpenIdVerifier.ProviderEndpoint,
            parameters));
    }

    private static async Task<IResult> CompleteSteamAsync(
        HttpContext context,
        IDataProtectionProvider dataProtection,
        SteamOpenIdVerifier verifier,
        ICurrentUserAccessor currentUser,
        CompleteExternalSignInUseCase useCase,
        AuthenticateAndSignIn signIn,
        IConfiguration configuration,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        string? protectedState = context.Request.Cookies[SteamStateCookieName];
        context.Response.Cookies.Delete(
            SteamStateCookieName,
            SteamStateCookieOptions(environment));
        if (!TryReadSteamState(protectedState, dataProtection, out SteamAuthState state) ||
            !string.Equals(state.Flow, "auth", StringComparison.Ordinal) ||
            !context.Request.Query.TryGetValue("n", out var suppliedNonce) ||
            suppliedNonce.Count != 1 ||
            !FixedTimeEquals(state.Nonce, suppliedNonce[0]))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "state_mismatch");
        }

        var assertion = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, var values) in context.Request.Query)
        {
            if (key.StartsWith("openid.", StringComparison.Ordinal))
            {
                if (values.Count != 1)
                {
                    return Problem(context, StatusCodes.Status401Unauthorized, "steam_assertion_invalid");
                }

                assertion[key] = values[0]!;
            }
        }

        SteamOpenIdVerification verification;
        try
        {
            verification = await verifier.VerifyAsync(
                assertion,
                SteamCallbackUrl(configuration, state.Nonce),
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            verification = SteamOpenIdVerification.Invalid;
        }

        if (!verification.Succeeded || verification.SteamId64 is not { } steamId64)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "steam_assertion_invalid");
        }

        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        ExternalSignInResult result = await useCase.ExecuteAsync(
            new CompleteExternalSignInCommand(
                ExternalProviderPolicy.Steam,
                steamId64,
                null,
                false,
                state.Mode,
                state.AgeGateAttested,
                state.BoundUserId is { } bound ? UserId.From(bound) : null,
                caller?.Id),
            cancellationToken);
        return await FinishExternalResultAsync(result, signIn, state.ReturnTo, cancellationToken);
    }

    private static async Task<IResult> CompleteAsync(
        HttpContext context,
        ICurrentUserAccessor currentUser,
        CompleteExternalSignInUseCase useCase,
        AuthenticateAndSignIn signIn,
        CancellationToken cancellationToken)
    {
        AuthenticateResult external = await context.AuthenticateAsync(
            IdentityConstants.ExternalScheme);
        string? requestedReturnTo = null;
        external.Properties?.Items.TryGetValue(PropertyReturnTo, out requestedReturnTo);
        string returnTo = ReturnToValidator.Sanitize(requestedReturnTo);

        try
        {
            if (!external.Succeeded || external.Principal is null ||
                !TryReadFlow(external.Properties, out ExternalFlow flow))
            {
                return Redirect("external_auth_failed", isError: true, returnTo);
            }

            ClaimsPrincipal principal = external.Principal;
            string? providerKey = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return Redirect("external_auth_failed", isError: true, returnTo);
            }

            CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
            ExternalSignInResult result = await useCase.ExecuteAsync(
                new CompleteExternalSignInCommand(
                    flow.Provider,
                    providerKey,
                    principal.FindFirstValue(ClaimTypes.Email),
                    string.Equals(
                        principal.FindFirstValue("email_verified"),
                        "true",
                        StringComparison.OrdinalIgnoreCase),
                    flow.Mode,
                    flow.AgeGateAttested,
                    flow.BoundUserId,
                    caller?.Id),
                cancellationToken);

            return await FinishExternalResultAsync(
                result,
                signIn,
                returnTo,
                cancellationToken);
        }
        finally
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme);
        }
    }

    private static async Task<IResult> FinishExternalResultAsync(
        ExternalSignInResult result,
        AuthenticateAndSignIn signIn,
        string returnTo,
        CancellationToken cancellationToken)
    {
        if (!result.Succeeded)
        {
            bool ageGate = result.Code == "age_gate_required";
            return Redirect(result.Code!, isError: !ageGate, returnTo);
        }

        if (result.ShouldSignIn && result.UserId is { } userId)
        {
            PostAuthenticationResult gate = await signIn.ExecuteAsync(userId, cancellationToken);
            if (gate is PostAuthenticationResult.Refused refused)
            {
                return Redirect(refused.Code, isError: true, returnTo);
            }
        }

        if (result.ReauthenticationToken is { } proof)
        {
            return Redirect(
                "reauth_ok",
                isError: false,
                returnTo,
                $"reauth={Uri.EscapeDataString(proof)}");
        }

        return Redirect(result.MethodLinked ? "linked" : "signed_in", false, returnTo);
    }

    private static bool TryReadFlow(
        AuthenticationProperties? properties,
        out ExternalFlow flow)
    {
        flow = default;
        if (properties is null ||
            !properties.Items.TryGetValue(PropertyProvider, out string? provider) ||
            !ExternalProviderPolicy.IsSupported(provider ?? string.Empty) ||
            !properties.Items.TryGetValue(PropertyMode, out string? modeValue) ||
            !Enum.TryParse(modeValue, ignoreCase: true, out ExternalStartMode mode) ||
            !properties.Items.TryGetValue(PropertyAgeGate, out string? ageGateValue) ||
            !bool.TryParse(ageGateValue, out bool ageGateAttested))
        {
            return false;
        }

        UserId? boundUserId = null;
        if (properties.Items.TryGetValue(PropertyBoundUserId, out string? boundValue))
        {
            if (!Guid.TryParse(boundValue, out Guid boundGuid))
            {
                return false;
            }

            boundUserId = UserId.From(boundGuid);
        }

        flow = new ExternalFlow(
            ExternalProviderPolicy.Canonicalize(provider!),
            mode,
            ageGateAttested,
            boundUserId);
        return true;
    }

    private static IResult Redirect(
        string code,
        bool isError,
        string returnTo,
        string? fragment = null)
    {
        var query = new QueryString()
            .Add(isError ? "error" : "status", code)
            .Add("next", ReturnToValidator.Sanitize(returnTo));
        string location = $"/auth/callback{query}";
        if (!string.IsNullOrEmpty(fragment))
        {
            location = $"{location}#{fragment}";
        }

        return Results.Redirect(location);
    }

    private static bool TryReadSteamState(
        string? protectedState,
        IDataProtectionProvider dataProtection,
        out SteamAuthState state)
    {
        state = new SteamAuthState(string.Empty, string.Empty, default, "/app", null, false);
        if (string.IsNullOrWhiteSpace(protectedState))
        {
            return false;
        }

        try
        {
            IDataProtector protector = dataProtection.CreateProtector(SteamStateProtectorPurpose);
            SteamAuthState? parsed = JsonSerializer.Deserialize<SteamAuthState>(
                protector.Unprotect(protectedState));
            if (parsed is null)
            {
                return false;
            }

            state = parsed;
            return true;
        }
        catch (Exception exception) when (
            exception is CryptographicException or JsonException or FormatException)
        {
            return false;
        }
    }

    private static CookieOptions SteamStateCookieOptions(IHostEnvironment environment) =>
        new()
        {
            HttpOnly = true,
            Secure = !environment.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Path = SteamStateCookiePath,
            MaxAge = TimeSpan.FromMinutes(10),
        };

    private static string SteamCallbackUrl(IConfiguration configuration, string nonce) =>
        QueryHelpers.AddQueryString(
            PublicOrigin(configuration) + SteamStateCookiePath + "/callback",
            "n",
            nonce);

    private static string SteamRealm(IConfiguration configuration)
    {
        string? configured = configuration["Steam:Realm"];
        return string.IsNullOrWhiteSpace(configured)
            ? PublicOrigin(configuration)
            : configured.TrimEnd('/');
    }

    private static string PublicOrigin(IConfiguration configuration)
    {
        string configured = configuration["PublicBaseUrl"] ?? "http://localhost:5173";
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("PublicBaseUrl must be an absolute HTTP(S) URL.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static List<string> ReadBinders(string? protectedValue, IDataProtector protector)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return [];
        }

        try
        {
            string json = protector.Unprotect(protectedValue);
            string[] values = JsonSerializer.Deserialize<string[]>(json) ?? [];
            return values
                .Where(value => IntentBinder.TryDecode(value, out _))
                .TakeLast(MaximumBinders)
                .ToList();
        }
        catch (Exception exception) when (
            exception is CryptographicException or JsonException or FormatException)
        {
            return [];
        }
    }

    private static void WriteBinders(
        HttpResponse response,
        List<string> binders,
        IDataProtector protector,
        IHostEnvironment environment)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = !environment.IsDevelopment(),
            SameSite = SameSiteMode.Strict,
            Path = IntentCookiePath,
        };

        if (binders.Count == 0)
        {
            options.Expires = DateTimeOffset.UnixEpoch;
            options.MaxAge = TimeSpan.Zero;
            response.Cookies.Append(IntentCookieName, string.Empty, options);
            return;
        }

        options.MaxAge = TimeSpan.FromMinutes(10);
        response.Cookies.Append(
            IntentCookieName,
            protector.Protect(JsonSerializer.Serialize(binders)),
            options);
    }

    private static IResult Problem(HttpContext context, int status, string code) =>
        Results.Json(
            new AuthProblem(
                "about:blank",
                status == StatusCodes.Status401Unauthorized ? "Unauthorized" :
                status == StatusCodes.Status409Conflict ? "Conflict" : "Bad Request",
                status,
                code,
                code is "invalid_credentials" or "reauth_required"
                    ? null
                    : context.TraceIdentifier),
            statusCode: status,
            contentType: "application/problem+json");

    private readonly record struct ExternalFlow(
        string Provider,
        ExternalStartMode Mode,
        bool AgeGateAttested,
        UserId? BoundUserId);

    private sealed record IntentRedemption(
        TokenRedemption Redemption,
        ExternalIntentPayload Payload);

    private sealed record SteamAuthState(
        string Flow,
        string Nonce,
        ExternalStartMode Mode,
        string ReturnTo,
        Guid? BoundUserId,
        bool AgeGateAttested);
}

public sealed record ExternalIntentRequest(
    string Provider,
    string Mode,
    string? ReturnTo,
    bool? AgeGateAttested,
    string? CurrentPassword,
    string? ReauthToken);

public sealed record ExternalIntentResponse(string Intent);
