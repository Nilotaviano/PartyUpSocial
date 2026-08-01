using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using NodaTime;
using NodaTime.Text;
using PartyUp.Api.Auth;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder auth = api.MapGroup("/auth");

        auth.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
            {
                AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
                return TypedResults.Ok(new AntiforgeryTokenResponse(tokens.RequestToken!));
            })
            .WithName(ApiOperationIds.GetAntiforgery)
            .Produces<AntiforgeryTokenResponse>()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        auth.MapGet("/session", GetSessionAsync)
            .WithName(ApiOperationIds.GetSession)
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        auth.MapPost("/register", RegisterAsync)
            .WithName(ApiOperationIds.Register)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        auth.MapPost("/login", LoginAsync)
            .WithName(ApiOperationIds.Login)
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/magic-link", RequestMagicLinkAsync)
            .WithName(ApiOperationIds.RequestMagicLink)
            .Produces(StatusCodes.Status202Accepted);

        auth.MapPost("/magic-link/consume", ConsumeMagicLinkAsync)
            .WithName(ApiOperationIds.ConsumeMagicLink)
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/password/forgot", ForgotPasswordAsync)
            .WithName(ApiOperationIds.ForgotPassword)
            .Produces(StatusCodes.Status202Accepted);

        auth.MapPost("/password/reset", ResetPasswordAsync)
            .WithName(ApiOperationIds.ResetPassword)
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/password/change", ChangePasswordAsync)
            .WithName(ApiOperationIds.ChangePassword)
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/password/set", SetPasswordAsync)
            .WithName(ApiOperationIds.SetPassword)
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/email/change", ChangeEmailAsync)
            .WithName(ApiOperationIds.ChangeEmail)
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/email/change/verify", VerifyEmailChangeAsync)
            .WithName("VerifyEmailChange")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/email/verify/request", RequestEmailVerificationAsync)
            .WithName(ApiOperationIds.RequestEmailVerification)
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/email/verify", VerifyEmailAsync)
            .WithName(ApiOperationIds.VerifyEmail)
            .Produces<SessionResponse>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/reauth", ReauthenticateAsync)
            .WithName(ApiOperationIds.Reauthenticate)
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces<ReauthenticationTokenResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/reauth/email", ReauthenticateByEmailAsync)
            .WithName(ApiOperationIds.ReauthenticateByEmail)
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapGet("/methods", ListAuthMethodsAsync)
            .WithName("GetAuthMethods")
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces<AuthMethodsResponse>();

        auth.MapDelete("/methods/{kind}", UnlinkAuthMethodAsync)
            .WithName(ApiOperationIds.DeleteAuthMethod)
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/logout", LogoutAsync)
            .WithName(ApiOperationIds.Logout)
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return api;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        HttpContext context,
        RegisterWithPasswordUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (request.AdditionalProperties is { Count: > 0 })
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_request");
        }

        RegistrationResult result = await useCase.ExecuteAsync(
            new RegisterWithPasswordCommand(
                request.Email,
                request.AgeGateAttested,
                ResolveLocale(request.Locale, context),
                request.Continuation),
            cancellationToken);
        return result.IsAccepted
            ? Results.Accepted(value: new StatusResponse("verification_sent"))
            : Problem(context, StatusCodes.Status400BadRequest, result.Code!);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        SignInWithPasswordUseCase useCase,
        AuthenticateAndSignIn signIn,
        IUserRepository users,
        ICurrentUserProjectionReader projections,
        ProviderCapabilities providers,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        PasswordSignInResult result = await useCase.ExecuteAsync(
            request.Email,
            request.Password,
            cancellationToken);
        return !result.Succeeded
            ? Problem(context, StatusCodes.Status401Unauthorized, "invalid_credentials")
            : await CreateSessionAsync(
                result.UserId!.Value,
                signIn,
                users,
                projections,
                providers,
                context,
                cancellationToken);
    }

    private static async Task<IResult> RequestMagicLinkAsync(
        MagicLinkRequest request,
        HttpContext context,
        RequestMagicLinkUseCase useCase,
        CancellationToken cancellationToken)
    {
        await useCase.ExecuteAsync(
            request.Email,
            ResolveLocale(request.Locale, context),
            request.Continuation,
            cancellationToken);
        return Results.Accepted(value: new StatusResponse("link_sent"));
    }

    private static async Task<IResult> ConsumeMagicLinkAsync(
        ConsumeMagicLinkRequest request,
        ConsumeMagicLinkUseCase useCase,
        AuthenticateAndSignIn signIn,
        IUserRepository users,
        ICurrentUserProjectionReader projections,
        ProviderCapabilities providers,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        MagicLinkConsumptionResult result = await useCase.ExecuteAsync(
            request.Token,
            request.AgeGateAttested == true,
            cancellationToken);
        if (!result.Succeeded)
        {
            int status = result.Code == "age_gate_required"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status401Unauthorized;
            return Problem(context, status, result.Code!);
        }

        return await CreateSessionAsync(
            result.UserId!.Value,
            signIn,
            users,
            projections,
            providers,
            context,
            result.Continuation,
            cancellationToken);
    }

    private static async Task<IResult> ForgotPasswordAsync(
        EmailRequest request,
        RequestPasswordResetUseCase useCase,
        CancellationToken cancellationToken)
    {
        await useCase.ExecuteAsync(request.Email, cancellationToken);
        return Results.Accepted(value: new StatusResponse("reset_sent"));
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        ResetPasswordUseCase useCase,
        AuthenticateAndSignIn signIn,
        IUserRepository users,
        ICurrentUserProjectionReader projections,
        ProviderCapabilities providers,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        PasswordResetResult result = await useCase.ExecuteAsync(
            request.Token,
            request.NewPassword,
            cancellationToken);
        return !result.Succeeded
            ? Problem(context, StatusCodes.Status409Conflict, result.Code!)
            : await CreateSessionAsync(
                result.UserId!.Value,
                signIn,
                users,
                projections,
                providers,
                context,
                cancellationToken);
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ICurrentUserAccessor currentUser,
        ChangePasswordUseCase useCase,
        AuthenticateAndSignIn signIn,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        AccountMutationResult result = await useCase.ExecuteAsync(
            caller.Id,
            request.CurrentPassword,
            request.NewPassword,
            cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status409Conflict, result.Code!);
        }

        PostAuthenticationResult gate = await signIn.ExecuteAsync(caller.Id, cancellationToken);
        return gate is PostAuthenticationResult.Admitted
            ? Results.NoContent()
            : GateProblem(context, gate);
    }

    private static async Task<IResult> SetPasswordAsync(
        SetPasswordRequest request,
        ICurrentUserAccessor currentUser,
        SetPasswordUseCase useCase,
        AuthenticateAndSignIn signIn,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        AccountMutationResult result = await useCase.ExecuteAsync(
            caller.Id,
            request.NewPassword,
            request.CurrentPassword,
            request.ReauthToken,
            cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status409Conflict, result.Code!);
        }

        PostAuthenticationResult gate = await signIn.ExecuteAsync(caller.Id, cancellationToken);
        return gate is PostAuthenticationResult.Admitted
            ? Results.NoContent()
            : GateProblem(context, gate);
    }

    private static async Task<IResult> ChangeEmailAsync(
        ChangeEmailRequest request,
        ICurrentUserAccessor currentUser,
        ChangeEmailUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        AccountMutationResult result = await useCase.RequestAsync(
            caller.Id,
            request.Email,
            request.CurrentPassword,
            request.ReauthToken,
            cancellationToken);
        return result.Succeeded
            ? Results.Accepted(value: new StatusResponse("verification_sent"))
            : Problem(context, StatusCodes.Status409Conflict, result.Code!);
    }

    private static async Task<IResult> VerifyEmailChangeAsync(
        TokenRequest request,
        ChangeEmailUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        AccountMutationResult result = await useCase.VerifyAsync(
            request.Token,
            cancellationToken);
        return result.Succeeded
            ? Results.NoContent()
            : Problem(context, StatusCodes.Status409Conflict, result.Code!);
    }

    private static async Task<IResult> RequestEmailVerificationAsync(
        ICurrentUserAccessor currentUser,
        IUserAccountGateway accounts,
        IUserRepository users,
        ISecurityTokenService tokens,
        IUnitOfWork unitOfWork,
        IEmailDispatcher emailDispatcher,
        AccountEmailOptions emailOptions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        IResult? malformedBody = await ValidateOptionalJsonBodyAsync(
            context,
            cancellationToken);
        if (malformedBody is not null)
        {
            return malformedBody;
        }

        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        AccountEmailInfo? email = await accounts.GetEmailAsync(caller.Id, cancellationToken);
        if (email is null)
        {
            return Problem(context, StatusCodes.Status409Conflict, "email_required");
        }

        if (!email.IsConfirmed)
        {
            IssuedToken issued = await unitOfWork.ExecuteAsync(
                ct => tokens.IssueAsync(
                    new IssueTokenRequest(
                        AuthTokenPurpose.EmailVerification,
                        caller.Id,
                        AccountInput.NormalizeEmail(email.Email),
                        Duration.FromHours(24),
                        Email: email.Email),
                    ct),
                cancellationToken);
            Locale locale = (await users.FindAsync(caller.Id, cancellationToken))?.Locale
                ?? AccountInput.Locale(null);
            await emailDispatcher.DispatchAsync(
                new EmailMessage(
                    email.Email,
                    locale,
                    EmailTemplate.EmailVerification,
                    AccountInput.LinkModel(emailOptions.PublicBaseUrl, issued.RawToken)),
                cancellationToken);
        }

        return Results.Accepted(value: new StatusResponse("verification_sent"));
    }

    private static async Task<IResult> VerifyEmailAsync(
        VerifyEmailRequest request,
        VerifyEmailUseCase useCase,
        AuthenticateAndSignIn signIn,
        IUserRepository users,
        ICurrentUserProjectionReader projections,
        ProviderCapabilities providers,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        EmailVerificationResult result = await useCase.ExecuteAsync(
            request.Token,
            request.Password,
            request.AgeGateAttested == true,
            cancellationToken);
        if (!result.Succeeded)
        {
            int status = result.Code == "age_gate_required"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return Problem(context, status, result.Code!);
        }

        if (result.ShouldSignIn)
        {
            return await CreateSessionAsync(
                result.UserId!.Value,
                signIn,
                users,
                projections,
                providers,
                context,
                result.Continuation,
                cancellationToken);
        }

        return result.AccountAlreadyExisted
            ? Results.Ok(new StatusResponse("account_exists"))
            : Results.NoContent();
    }

    private static async Task<IResult> ReauthenticateAsync(
        ReauthenticateRequest request,
        ICurrentUserAccessor currentUser,
        ReauthenticateUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        ReauthenticationResult result = await useCase.WithPasswordAsync(
            caller.Id,
            request.CurrentPassword,
            cancellationToken);
        return result.Succeeded
            ? Results.Ok(new ReauthenticationTokenResponse(result.Token!))
            : Problem(context, StatusCodes.Status409Conflict, result.Code!);
    }

    private static async Task<IResult> ReauthenticateByEmailAsync(
        ICurrentUserAccessor currentUser,
        ReauthenticateUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        IResult? malformedBody = await ValidateOptionalJsonBodyAsync(
            context,
            cancellationToken);
        if (malformedBody is not null)
        {
            return malformedBody;
        }

        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        ReauthenticationResult result = await useCase.RequestEmailAsync(
            caller.Id,
            cancellationToken);
        return result.Succeeded
            ? Results.Accepted(value: new StatusResponse("confirmation_sent"))
            : Problem(context, StatusCodes.Status409Conflict, result.Code!);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        IResult? malformedBody = await ValidateOptionalJsonBodyAsync(
            context,
            cancellationToken);
        if (malformedBody is not null)
        {
            return malformedBody;
        }

        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListAuthMethodsAsync(
        ICurrentUserAccessor currentUser,
        ListAuthMethodsUseCase useCase,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        AuthMethodsResult result = await useCase.ExecuteAsync(caller.Id, cancellationToken);
        return Results.Ok(new AuthMethodsResponse(
            result.Methods.Select(method => new AuthMethodResponse(
                method.Kind,
                method.Linked,
                method.Removable,
                method.Detail)).ToArray()));
    }

    private static async Task<IResult> UnlinkAuthMethodAsync(
        string kind,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        ReauthenticateUseCase reauthenticate,
        UnlinkAuthMethodUseCase useCase,
        CancellationToken cancellationToken)
    {
        IResult? malformedBody = await ValidateOptionalJsonBodyAsync(context, cancellationToken);
        if (malformedBody is not null)
        {
            return malformedBody;
        }

        AuthMethodProofRequest? proof = await ReadOptionalBodyAsync<AuthMethodProofRequest>(
            context,
            cancellationToken);
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        bool proven = await reauthenticate.ValidateProofAsync(
            caller.Id,
            proof?.CurrentPassword,
            proof?.ReauthToken,
            cancellationToken);
        UnlinkAuthMethodResult result = await useCase.ExecuteAsync(
            new UnlinkAuthMethodCommand(caller.Id, kind, proven),
            cancellationToken);
        if (result.Succeeded)
        {
            return Results.NoContent();
        }

        int status = result.Code switch
        {
            "method_not_removable" or "invalid_request" => StatusCodes.Status400BadRequest,
            "not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict,
        };
        return Problem(context, status, result.Code!);
    }

    internal static async Task<IResult?> ValidateOptionalJsonBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            return null;
        }

        context.Request.EnableBuffering();
        long initialPosition = context.Request.Body.Position;
        try
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, cancellationToken);
            if (body.Length == 0)
            {
                return null;
            }

            body.Position = 0;
            using JsonDocument _ = await JsonDocument.ParseAsync(
                body,
                cancellationToken: cancellationToken);
            return null;
        }
        catch (JsonException)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_request");
        }
        finally
        {
            context.Request.Body.Position = initialPosition;
        }
    }

    internal static async Task<T?> ReadOptionalBodyAsync<T>(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            return default;
        }

        context.Request.EnableBuffering();
        if (context.Request.Body.Length == 0)
        {
            return default;
        }

        long initialPosition = context.Request.Body.Position;
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                JsonSerializerOptions.Web,
                cancellationToken);
        }
        finally
        {
            context.Request.Body.Position = initialPosition;
        }
    }

    private static async Task<Ok<SessionResponse>> GetSessionAsync(
        HttpContext context,
        ICurrentUserAccessor currentUserAccessor,
        IUserRepository users,
        ProviderCapabilities providers,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return TypedResults.Ok(SessionResponse.Anonymous(providers.Values));
        }

        CurrentUser? currentUser = await currentUserAccessor.GetAsync(cancellationToken);
        if (currentUser is null)
        {
            return TypedResults.Ok(SessionResponse.Anonymous(providers.Values));
        }

        User? user = await users.FindAsync(currentUser.Id, cancellationToken);
        return TypedResults.Ok(user is null
            ? SessionResponse.Anonymous(providers.Values)
            : SessionResponse.ForAuthenticatedUser(user, currentUser.IsAdmin, providers.Values));
    }

    private static async Task<IResult> CreateSessionAsync(
        UserId userId,
        AuthenticateAndSignIn signIn,
        IUserRepository users,
        ICurrentUserProjectionReader projections,
        ProviderCapabilities providers,
        HttpContext context,
        CancellationToken cancellationToken)
        => await CreateSessionAsync(
            userId,
            signIn,
            users,
            projections,
            providers,
            context,
            continuation: null,
            cancellationToken);

    private static async Task<IResult> CreateSessionAsync(
        UserId userId,
        AuthenticateAndSignIn signIn,
        IUserRepository users,
        ICurrentUserProjectionReader projections,
        ProviderCapabilities providers,
        HttpContext context,
        string? continuation,
        CancellationToken cancellationToken)
    {
        PostAuthenticationResult gate = await signIn.ExecuteAsync(userId, cancellationToken);
        if (gate is not PostAuthenticationResult.Admitted)
        {
            return GateProblem(context, gate);
        }

        User? user = await users.FindAsync(userId, cancellationToken);
        CurrentUserProjection? projection = await projections.ReadAsync(
            userId,
            cancellationToken);
        return user is null
            ? Problem(context, StatusCodes.Status401Unauthorized, "invalid_credentials")
            : Results.Ok(SessionResponse.ForAuthenticatedUser(
                user,
                projection?.IsAdmin == true,
                providers.Values,
                continuation));
    }

    private static IResult GateProblem(HttpContext context, PostAuthenticationResult result)
    {
        string code = (result as PostAuthenticationResult.Refused)?.Code
            ?? "invalid_credentials";
        int status = code == "invalid_credentials"
            ? StatusCodes.Status401Unauthorized
            : StatusCodes.Status403Forbidden;
        return Problem(context, status, code);
    }

    private static IResult Problem(HttpContext context, int status, string code) =>
        Results.Json(
            new AuthProblem(
                "about:blank",
                status == StatusCodes.Status401Unauthorized ? "Unauthorized" :
                status == StatusCodes.Status403Forbidden ? "Forbidden" :
                status == StatusCodes.Status409Conflict ? "Conflict" : "Bad Request",
                status,
                code,
                code is "invalid_credentials" or "reauth_required"
                    ? null
                    : context.TraceIdentifier),
            statusCode: status,
            contentType: "application/problem+json");

    private static string ResolveLocale(string? requested, HttpContext context)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return EmbeddedEmailTemplateRenderer.CanonicalizeLocale(requested);
        }

        string acceptLanguage = context.Request.Headers.AcceptLanguage.ToString();
        foreach (string part in acceptLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string tag = part.Split(';', 2)[0].Trim();
            string canonical = EmbeddedEmailTemplateRenderer.CanonicalizeLocale(tag);
            if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
                tag.StartsWith("es", StringComparison.OrdinalIgnoreCase) ||
                tag.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
            {
                return canonical;
            }
        }

        return "en";
    }
}

public sealed record AntiforgeryTokenResponse(string Token);

public sealed class RegisterRequest
{
    public string Email { get; init; } = string.Empty;

    public bool AgeGateAttested { get; init; }

    public string? Locale { get; init; }

    public string? Continuation { get; init; }

    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, System.Text.Json.JsonElement>? AdditionalProperties { get; init; }
}

public sealed record LoginRequest(string Email, string Password);

public sealed record MagicLinkRequest(
    string Email,
    string? Locale,
    string? Continuation = null);

public sealed record ConsumeMagicLinkRequest(string Token, bool? AgeGateAttested);

public sealed record EmailRequest(string Email);

public sealed record TokenRequest(string Token);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record SetPasswordRequest(
    string NewPassword,
    string? CurrentPassword,
    string? ReauthToken);

public sealed record ChangeEmailRequest(
    string Email,
    string? CurrentPassword,
    string? ReauthToken);

public sealed record VerifyEmailRequest(
    string Token,
    string? Password,
    bool? AgeGateAttested);

public sealed record ReauthenticateRequest(string CurrentPassword);

public sealed record ReauthenticationTokenResponse(string ReauthToken);

public sealed record AuthMethodProofRequest(string? CurrentPassword, string? ReauthToken);

public sealed record AuthMethodResponse(
    string Kind,
    bool Linked,
    bool Removable,
    string? Detail);

public sealed record AuthMethodsResponse(IReadOnlyList<AuthMethodResponse> Methods);

public sealed record StatusResponse(string Status);

public sealed record AuthProblem(
    string Type,
    string Title,
    int Status,
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TraceId);

public sealed class ProviderCapabilities
{
    public ProviderCapabilities(IConfiguration configuration)
    {
        var values = new List<string> { "password", "magicLink" };
        if (!string.IsNullOrWhiteSpace(configuration["Google:ClientId"]))
        {
            values.Add("google");
        }

        if (configuration.GetValue("Steam:Enabled", true))
        {
            values.Add("steam");
        }

        Values = values.AsReadOnly();
    }

    public IReadOnlyList<string> Values { get; }
}

public sealed record SessionResponse(
    bool Authenticated,
    Guid? UserId,
    IReadOnlyList<string> Providers,
    string? State,
    bool IsAdmin,
    OnboardingResponse? Onboarding,
    PendingDeletionResponse? PendingDeletion,
    ProfileResponse? Profile,
    string? Continuation)
{
    public static SessionResponse Anonymous(IReadOnlyList<string> providers) =>
        new(false, null, providers, null, false, null, null, null, null);

    public static SessionResponse ForAuthenticatedUser(
        User user,
        bool isAdmin,
        IReadOnlyList<string> providers,
        string? continuation = null)
    {
        OnboardingResponse? onboarding = user.Lifecycle == UserLifecycle.Onboarding
            ? new OnboardingResponse(["ageGate", "auth"], "username")
            : null;
        PendingDeletionResponse? pendingDeletion =
            user.Lifecycle == UserLifecycle.PendingDeletion && user.GraceEndsAt is { } graceEndsAt
                ? new PendingDeletionResponse(InstantPattern.ExtendedIso.Format(graceEndsAt))
                : null;
        ProfileResponse? profile = user.IsProfileComplete
            ? ProfileResponse.From(user)
            : null;

        return new SessionResponse(
            true,
            user.Id.Value,
            providers,
            StateName(user.Lifecycle),
            isAdmin,
            onboarding,
            pendingDeletion,
            profile,
            continuation);
    }

    private static string StateName(UserLifecycle lifecycle) => lifecycle switch
    {
        UserLifecycle.Onboarding => "onboarding",
        UserLifecycle.Active => "active",
        UserLifecycle.PendingDeletion => "pending_deletion",
        UserLifecycle.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, null),
    };
}

public sealed record OnboardingResponse(
    IReadOnlyList<string> CompletedSteps,
    string NextStep);

public sealed record PendingDeletionResponse(string GraceEndsAt);

public sealed record ProfileResponse(
    string Username,
    string DisplayName,
    AvatarResponse Avatar,
    string TimeZone,
    string Locale,
    string FriendCode)
{
    public static ProfileResponse From(User user) =>
        new(
            user.Username!.Value.Value,
            user.DisplayName!.Value.Value,
            AvatarResponse.From(user.Avatar!.Value),
            user.TimeZone!.Id,
            user.Locale!.Value.Value,
            user.FriendCode!.Value.Value);
}

public sealed record AvatarResponse(
    string Kind,
    string? Emoji,
    Guid? SourcePlatformLinkId)
{
    public static AvatarResponse From(Avatar avatar) => avatar.IsPlatform
        ? new("platform", null, avatar.SourcePlatformLinkId!.Value.Value)
        : new("emoji", avatar.Emoji, null);
}
