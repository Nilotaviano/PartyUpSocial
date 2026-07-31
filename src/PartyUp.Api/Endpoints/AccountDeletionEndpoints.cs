using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using PartyUp.Api.Auth;
using PartyUp.Application.Deletion;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.Endpoints;

public static class AccountDeletionEndpoints
{
    private const string StateCookieName = "pu.extdel";
    private const string StateCookiePath = "/api/v1/account-deletion-requests/steam";
    private const string StateProtectorPurpose = "PartyUp.SteamState.Deletion";

    public static RouteGroupBuilder MapAccountDeletionEndpoints(
        this RouteGroupBuilder api,
        bool steamEnabled,
        IHostEnvironment environment)
    {
        RouteGroupBuilder requests = api.MapGroup("/account-deletion-requests");
        api.MapPost("/account-deletion-requests", RequestAsync)
            .WithName(ApiOperationIds.RequestAccountDeletion)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        requests.MapPost("/confirm", ConfirmAsync)
            .WithName("ConfirmAccountDeletion")
            .Produces<AccountDeletionResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        if (steamEnabled)
        {
            requests.MapGet("/steam/start", (
                    HttpContext context,
                    IDataProtectionProvider dataProtection,
                    ExternalAuthOriginOptions origins) =>
                    StartSteam(context, dataProtection, origins, environment))
                .WithName(ApiOperationIds.StartSteamDeletion)
                .Produces(StatusCodes.Status302Found);
            requests.MapGet("/steam/callback", (
                    HttpContext context,
                    IDataProtectionProvider dataProtection,
                    ISteamOpenIdVerifier verifier,
                    IssueSteamDeletionConfirmationUseCase useCase,
                    ExternalAuthOriginOptions origins,
                    CancellationToken cancellationToken) =>
                    CompleteSteamAsync(
                        context,
                        dataProtection,
                        verifier,
                        useCase,
                        origins,
                        environment,
                        cancellationToken))
                .WithName(ApiOperationIds.CompleteSteamDeletion)
                .Produces(StatusCodes.Status302Found)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized);
        }

        return api;
    }

    private static async Task<IResult> RequestAsync(
        PublicDeletionRequest request,
        HttpContext context,
        RequestPublicDeletionUseCase useCase,
        CancellationToken cancellationToken)
    {
        await useCase.ExecuteAsync(
            new RequestPublicDeletionCommand(
                request.Email,
                ResolveLocale(context.Request.Headers.AcceptLanguage.ToString())),
            cancellationToken);
        return Results.Accepted(value: new StatusResponse("confirmation_sent"));
    }

    private static async Task<IResult> ConfirmAsync(
        PublicDeletionConfirmationRequest request,
        HttpContext context,
        ConfirmPublicDeletionUseCase useCase,
        CancellationToken cancellationToken)
    {
        AccountDeletionRequestResult result = await useCase.ExecuteAsync(
            request.Token,
            cancellationToken);
        return result.Succeeded
            ? Results.Accepted(value: new AccountDeletionResponse(
                NodaTime.Text.InstantPattern.ExtendedIso.Format(result.GraceEndsAt!.Value)))
            : Problem(context, StatusCodes.Status400BadRequest, result.Code!);
    }

    private static IResult StartSteam(
        HttpContext context,
        IDataProtectionProvider dataProtection,
        ExternalAuthOriginOptions origins,
        IHostEnvironment environment)
    {
        string nonce = IntentBinder.Encode(IntentBinder.Create());
        string callback = SteamCallbackUrl(origins, nonce);
        var state = new SteamDeletionState("deletion", nonce);
        IDataProtector protector = dataProtection.CreateProtector(StateProtectorPurpose);
        context.Response.Cookies.Append(
            StateCookieName,
            protector.Protect(JsonSerializer.Serialize(state)),
            StateCookieOptions(environment));

        var parameters = new Dictionary<string, string?>
        {
            ["openid.ns"] = SteamOpenIdVerifier.OpenIdNamespace,
            ["openid.mode"] = "checkid_setup",
            ["openid.identity"] = SteamOpenIdVerifier.OpenIdNamespace + "/identifier_select",
            ["openid.claimed_id"] = SteamOpenIdVerifier.OpenIdNamespace + "/identifier_select",
            ["openid.return_to"] = callback,
            ["openid.realm"] = origins.SteamRealm,
        };
        return Results.Redirect(QueryHelpers.AddQueryString(
            SteamOpenIdVerifier.ProviderEndpoint,
            parameters));
    }

    private static async Task<IResult> CompleteSteamAsync(
        HttpContext context,
        IDataProtectionProvider dataProtection,
        ISteamOpenIdVerifier verifier,
        IssueSteamDeletionConfirmationUseCase useCase,
        ExternalAuthOriginOptions origins,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        string? protectedState = context.Request.Cookies[StateCookieName];
        context.Response.Cookies.Delete(StateCookieName, StateCookieOptions(environment));
        if (!TryReadState(protectedState, dataProtection, out SteamDeletionState state) ||
            !string.Equals(state.Flow, "deletion", StringComparison.Ordinal) ||
            !context.Request.Query.TryGetValue("n", out var suppliedNonce) ||
            suppliedNonce.Count != 1 ||
            !FixedTimeEquals(state.Nonce, suppliedNonce[0]))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "state_mismatch");
        }

        var assertion = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, var values) in context.Request.Query)
        {
            if (!key.StartsWith("openid.", StringComparison.Ordinal))
            {
                continue;
            }

            if (values.Count != 1)
            {
                return Problem(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "steam_assertion_invalid");
            }

            assertion[key] = values[0]!;
        }

        SteamOpenIdVerification verification;
        try
        {
            verification = await verifier.VerifyAsync(
                assertion,
                SteamCallbackUrl(origins, state.Nonce),
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            verification = SteamOpenIdVerification.Invalid;
        }

        if (!verification.Succeeded || verification.SteamId64 is not { } steamId64)
        {
            return Problem(
                context,
                StatusCodes.Status401Unauthorized,
                "steam_assertion_invalid");
        }

        SteamDeletionConfirmationResult result = await useCase.ExecuteAsync(
            steamId64,
            cancellationToken);
        return result.Succeeded
            ? Results.Redirect(
                $"/account-deletion/confirm#token={Uri.EscapeDataString(result.Token!)}")
            : Results.Redirect("/account-deletion/confirm#error=account_not_found");
    }

    private static bool TryReadState(
        string? protectedState,
        IDataProtectionProvider dataProtection,
        out SteamDeletionState state)
    {
        state = new SteamDeletionState(string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(protectedState))
        {
            return false;
        }

        try
        {
            IDataProtector protector = dataProtection.CreateProtector(StateProtectorPurpose);
            SteamDeletionState? parsed = JsonSerializer.Deserialize<SteamDeletionState>(
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

    private static CookieOptions StateCookieOptions(IHostEnvironment environment) =>
        new()
        {
            HttpOnly = true,
            Secure = !environment.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Path = StateCookiePath,
            MaxAge = TimeSpan.FromMinutes(10),
        };

    private static string SteamCallbackUrl(ExternalAuthOriginOptions origins, string nonce) =>
        QueryHelpers.AddQueryString(
            origins.PublicOrigin + StateCookiePath + "/callback",
            "n",
            nonce);

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

    private static string ResolveLocale(string acceptLanguage)
    {
        foreach (string part in acceptLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string tag = part.Split(';', 2)[0].Trim();
            if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
                tag.StartsWith("es", StringComparison.OrdinalIgnoreCase) ||
                tag.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
            {
                return PartyUp.Application.Email.EmbeddedEmailTemplateRenderer
                    .CanonicalizeLocale(tag);
            }
        }

        return "en";
    }

    private static IResult Problem(HttpContext context, int status, string code) =>
        Results.Json(
            new AuthProblem(
                "about:blank",
                status == StatusCodes.Status401Unauthorized ? "Unauthorized" : "Bad Request",
                status,
                code,
                context.TraceIdentifier),
            statusCode: status,
            contentType: "application/problem+json");

    private sealed record SteamDeletionState(string Flow, string Nonce);
}

public sealed record PublicDeletionRequest(string Email);

public sealed record PublicDeletionConfirmationRequest(string Token);
