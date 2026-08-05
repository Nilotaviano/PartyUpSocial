using NodaTime;
using PartyUp.Api.Contracts.Sessions;
using PartyUp.Api.ErrorHandling;
using PartyUp.Api.Security;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// The guest bearer's own resource surface (PHASE4_DESIGN.md §8.1). These routes authorize by matching
/// link cookie rather than <c>RequireOnboarded</c> — a guest is never a registered, onboarded user —
/// and carry no age gate (D-124). They still sit under <c>/api/v1</c> and inherit the group's
/// antiforgery filter on unsafe methods.
/// </summary>
public static class GuestSessionEndpoints
{
    public static RouteGroupBuilder MapGuestSessionEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder guest = api.MapGroup("/guest/{guestLinkId:guid}");

        Common(guest.MapGet("/session", GetGuestSessionAsync)
                .WithName(ApiOperationIds.GetGuestSession)
                .Produces<GuestSessionResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(guest.MapPost("/claim", ClaimGuestSessionLinkAsync)
                .WithName(ApiOperationIds.ClaimGuestSessionLink)
                .Produces<GuestSessionResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(guest.MapPut("/display-name", RenameGuestSessionIdentityAsync)
                .WithName(ApiOperationIds.RenameGuestSessionIdentity)
                .Produces<GuestSessionResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(guest.MapPut("/participation", RespondAsGuestAsync)
                .WithName(ApiOperationIds.RespondAsGuest)
                .Produces<GuestSessionResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        return api;
    }

    private static async Task<IResult> GetGuestSessionAsync(
        Guid guestLinkId,
        GuestSessionUseCases useCase,
        GuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        GuestLinkCredential? credential = ReadCredential(context, guestLinkId, protector, clock);
        if (credential is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, "session_not_found");
        }

        GuestSessionResult result = await useCase.GetGuestSessionAsync(
            credential.SessionId, credential.GuestLinkId, cancellationToken);
        if (!result.Found)
        {
            return Problem(context, StatusCodes.Status404NotFound, "session_not_found");
        }

        if (result.View != GuestLinkView.LiveView)
        {
            // A-074 (b)/(d): terminal notice regardless of the incoming credential mode; the
            // response re-issues a bounded mode=terminal credential so the bearer can still reach
            // the tokenless notice without their original URL.
            ReissueTerminalCredential(context, protector, clock, credential);
            return Results.Ok(GuestSessionResponse.Terminal(credential.SessionId, result.View));
        }

        if (credential.Mode != GuestCredentialMode.Active)
        {
            // A-074 (c): a terminal-mode credential against a still-Active, not-ended link/session is
            // forged-or-stale — fail closed rather than reveal that the underlying link is fine.
            return Problem(context, StatusCodes.Status404NotFound, "session_not_found");
        }

        return Results.Ok(GuestSessionResponse.Live(result.Live!));
    }

    private static async Task<IResult> ClaimGuestSessionLinkAsync(
        Guid guestLinkId,
        ClaimGuestSessionRequest request,
        GuestSessionUseCases useCase,
        ISessionReadRepository reads,
        GuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await MutateAsync(
            guestLinkId,
            useCase,
            reads,
            protector,
            clock,
            context,
            async (sessionId, linkId, ct) =>
            {
                ParticipationStatus? response = SessionWireFormat.ParseRsvpResponse(request.Response)
                    ?? (request.Response == "Pending" ? ParticipationStatus.Pending : null);
                if (response is null || string.IsNullOrWhiteSpace(request.DisplayName))
                {
                    throw new DomainException(DomainError.ValidationFailed, "A guest claims with a display name and response (D-017).");
                }

                DisplayName displayName;
                try
                {
                    displayName = DisplayName.Create(request.DisplayName);
                }
                catch (DomainException)
                {
                    throw new DomainException(DomainError.ValidationFailed, "Invalid display name.");
                }

                GuestCommandResult result = await useCase.ClaimGuestAsync(
                    sessionId, linkId, displayName, response.Value, ct);
                return result;
            },
            cancellationToken);

    private static async Task<IResult> RenameGuestSessionIdentityAsync(
        Guid guestLinkId,
        RenameGuestSessionRequest request,
        GuestSessionUseCases useCase,
        ISessionReadRepository reads,
        GuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await MutateAsync(
            guestLinkId,
            useCase,
            reads,
            protector,
            clock,
            context,
            async (sessionId, linkId, ct) =>
            {
                DisplayName displayName;
                try
                {
                    displayName = DisplayName.Create(request.DisplayName);
                }
                catch (DomainException)
                {
                    throw new DomainException(DomainError.ValidationFailed, "Invalid display name.");
                }

                return await useCase.RenameGuestAsync(sessionId, linkId, displayName, ct);
            },
            cancellationToken);

    private static async Task<IResult> RespondAsGuestAsync(
        Guid guestLinkId,
        RespondAsGuestRequest request,
        GuestSessionUseCases useCase,
        ISessionReadRepository reads,
        GuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await MutateAsync(
            guestLinkId,
            useCase,
            reads,
            protector,
            clock,
            context,
            async (sessionId, linkId, ct) =>
            {
                ParticipationStatus? response = SessionWireFormat.ParseRsvpResponse(request.Response);
                if (response is null)
                {
                    throw new DomainException(DomainError.ValidationFailed, "A guest responds Going, Maybe or Declined (D-017).");
                }

                return await useCase.RespondAsGuestAsync(sessionId, linkId, response.Value, ct);
            },
            cancellationToken);

    /// <summary>
    /// The shared A-074 mutation path: resolve access first (never invoking the domain mutation for a
    /// terminal link, so a stale credential can never even attempt a write), then run the caller's
    /// command and re-render the fresh <see cref="GuestSessionResponse"/> from the aggregate it returns.
    /// </summary>
    private static async Task<IResult> MutateAsync(
        Guid guestLinkId,
        GuestSessionUseCases useCase,
        ISessionReadRepository reads,
        GuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        Func<SessionId, GuestLinkId, CancellationToken, Task<GuestCommandResult>> mutate,
        CancellationToken cancellationToken)
    {
        GuestLinkCredential? credential = ReadCredential(context, guestLinkId, protector, clock);
        if (credential is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, "session_not_found");
        }

        GuestSessionResult resolved = await useCase.GetGuestSessionAsync(
            credential.SessionId, credential.GuestLinkId, cancellationToken);
        if (!resolved.Found)
        {
            return Problem(context, StatusCodes.Status404NotFound, "session_not_found");
        }

        if (resolved.View != GuestLinkView.LiveView || credential.Mode != GuestCredentialMode.Active)
        {
            // A-074 (b)/(c)/(d): a terminal link/session, or a terminal-mode credential against a
            // still-live link/session, never reaches the domain mutation — fail closed with 404
            // rather than reveal the underlying state via a 409.
            return Problem(context, StatusCodes.Status404NotFound, "session_not_found");
        }

        GuestCommandResult result;
        try
        {
            result = await mutate(credential.SessionId, credential.GuestLinkId, cancellationToken);
        }
        catch (DomainException ex)
        {
            return DomainProblem(context, ex);
        }

        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status404NotFound, result.Code!);
        }

        GuestSessionView? live = await reads.FindGuestViewAsync(credential.GuestLinkId, cancellationToken);
        return live is null
            ? Problem(context, StatusCodes.Status404NotFound, "session_not_found")
            : Results.Ok(GuestSessionResponse.Live(live));
    }

    private static void ReissueTerminalCredential(
        HttpContext context,
        GuestLinkCredentialProtector protector,
        IClock clock,
        GuestLinkCredential credential)
    {
        Instant now = clock.GetCurrentInstant();
        var terminal = new GuestLinkCredential(
            credential.GuestLinkId,
            credential.SessionId,
            GuestCredentialMode.Terminal,
            now,
            now + GuestLinkCredentialProtector.TerminalLifetime);

        bool isDevelopment = context.RequestServices
            .GetRequiredService<IWebHostEnvironment>().IsDevelopment();
        string guestLinkIdN = credential.GuestLinkId.Value.ToString("N");
        context.Response.Cookies.Append(
            $"pu.g.{guestLinkIdN}",
            protector.Protect(terminal),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = !isDevelopment,
                SameSite = SameSiteMode.Lax,
                Path = $"{ApiRoutes.V1}/guest/{guestLinkIdN}",
                MaxAge = GuestLinkCredentialProtector.TerminalLifetime.ToTimeSpan(),
            });
    }

    private static GuestLinkCredential? ReadCredential(
        HttpContext context,
        Guid guestLinkId,
        GuestLinkCredentialProtector protector,
        IClock clock)
    {
        string cookieName = $"pu.g.{guestLinkId:N}";
        if (!context.Request.Cookies.TryGetValue(cookieName, out string? cookieValue) || cookieValue is null)
        {
            return null;
        }

        GuestLinkCredential? credential = protector.Unprotect(cookieValue);
        if (credential is null ||
            credential.GuestLinkId.Value != guestLinkId ||
            credential.IsExpired(clock))
        {
            return null;
        }

        return credential;
    }

    private static IResult DomainProblem(HttpContext context, DomainException exception)
    {
        (int status, string code) = exception.Error switch
        {
            DomainError.Conflict => (StatusCodes.Status409Conflict, "guest_link_claimed"),
            DomainError.Forbidden => (StatusCodes.Status409Conflict, "guest_link_terminal"),
            DomainError.WindowClosed or DomainError.TerminalState =>
                (StatusCodes.Status422UnprocessableEntity, "session_responses_closed"),
            _ => (StatusCodes.Status400BadRequest, "session_validation"),
        };
        return Problem(context, status, code);
    }

    private static IResult Problem(HttpContext context, int status, string code) =>
        PartyUpProblemResults.Create(context, status, code);

    private static RouteHandlerBuilder Common(RouteHandlerBuilder builder) =>
        builder
            .ProducesPartyUpProblem(StatusCodes.Status429TooManyRequests)
            .ProducesPartyUpProblem(StatusCodes.Status500InternalServerError);

    private static RouteHandlerBuilder Unsafe(RouteHandlerBuilder builder) =>
        Common(builder)
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest)
            .ProducesPartyUpProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesPartyUpProblem(StatusCodes.Status503ServiceUnavailable);
}
