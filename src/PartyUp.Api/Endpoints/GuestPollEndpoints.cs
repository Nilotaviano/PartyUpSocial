using NodaTime;
using PartyUp.Api.Auth;
using PartyUp.Api.Contracts.Polls;
using PartyUp.Api.ErrorHandling;
using PartyUp.Api.Security;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// The poll guest bearer's own resource surface (PHASE6_DESIGN.md §4, ticket-P6-S4b) — parallel
/// nested resources on the shipped <c>/guest/{guestLinkId}</c> group (<see cref="GuestSessionEndpoints"/>'s
/// session handlers and <c>GuestSessionResponse</c> stay untouched). Authorizes by matching the
/// poll-purpose credential cookie, never <c>RequireOnboarded</c>: a guest is never a registered,
/// onboarded user, and carries no age gate (D-124). A session-typed credential structurally cannot
/// decrypt under <see cref="PollGuestLinkCredentialProtector"/>'s purpose, so it 404s here exactly
/// like a missing one — and vice versa on <see cref="GuestSessionEndpoints"/>.
/// </summary>
public static class GuestPollEndpoints
{
    public static RouteGroupBuilder MapGuestPollEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder guest = api.MapGroup("/guest/{guestLinkId:guid}");

        Common(guest.MapGet("/poll", GetGuestPollAsync)
                .WithName(ApiOperationIds.GetGuestPollView)
                .Produces<GuestPollResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(guest.MapPost("/poll/claim", ClaimGuestPollLinkAsync)
                .WithName(ApiOperationIds.ClaimGuestPollLink)
                .Produces<GuestPollResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(guest.MapPut("/poll/options/{optionId:guid}/vote", VoteOnPollOptionAsGuestAsync)
                .WithName(ApiOperationIds.VoteOnPollAsGuest)
                .Produces<GuestPollResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(guest.MapPost("/poll/options", SuggestPollOptionAsGuestAsync)
                .WithName(ApiOperationIds.SuggestPollOptionAsGuest)
                .Produces<GuestPollResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(guest.MapPut("/poll/display-name", RenameGuestPollIdentityAsync)
                .WithName(ApiOperationIds.RenameGuestPollIdentity)
                .Produces<GuestPollResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        // D-225: the poll arm's upgrade — the only guest-poll route that also requires an
        // onboarded account, mirroring GuestSessionEndpoints' /upgrade.
        Unsafe(guest.MapPost("/poll/upgrade", UpgradeGuestPollAsync)
                .WithName(ApiOperationIds.UpgradeGuestPoll)
                .RequireAuthorization(AuthorizationPolicies.RequireOnboarded)
                .Produces<GuestPollUpgradeResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status401Unauthorized)
            .ProducesPartyUpProblem(StatusCodes.Status403Forbidden)
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        return api;
    }

    // ---- handlers ---------------------------------------------------------------------------

    private static async Task<IResult> GetGuestPollAsync(
        Guid guestLinkId,
        IPollRepository polls,
        ISessionRepository sessions,
        GuestPollReadUseCases reads,
        PollGuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        PollGuestLinkCredential? credential = ReadCredential(context, guestLinkId, protector, clock);
        if (credential is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        Poll? poll = await polls.FindAsync(credential.PollId, cancellationToken);
        if (poll is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        // §5.4 handover: finalization re-parents EVERY one of the poll's guest links (including one
        // already revoked pre-finalization, D-044) onto the created session with State preserved,
        // and clears the poll's own collection — so once Finalized, resolution runs against the
        // session it created, not the poll (whose ResolveGuestLink would always throw NotFound for a
        // link it no longer carries), and a pre-finalization revocation still serves its own notice
        // rather than being swallowed by the finalized-handover branch.
        if (poll.Status == PollStatus.Finalized)
        {
            return await ResolveFinalizedNoticeAsync(poll, sessions, credential, protector, clock, context, cancellationToken);
        }

        GuestLinkView view;
        try
        {
            view = poll.ResolveGuestLink(credential.GuestLinkId, clock);
        }
        catch (DomainException)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        if (view != GuestLinkView.LiveView)
        {
            ReissueTerminalCredential(context, protector, clock, credential);
            return Results.Ok(GuestPollResponse.Terminal(credential.PollId.Value, view));
        }

        if (credential.Mode != GuestCredentialMode.Active)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        GuestPollDetail? detail = await reads.GetGuestPollDetailAsync(credential.PollId, credential.GuestLinkId, cancellationToken);
        if (detail is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        return Results.Ok(GuestPollResponse.Live(detail));
    }

    /// <summary>
    /// The Finalized-poll branch: resolves the link's notice against the SESSION it now lives on
    /// rather than assuming "finalized" unconditionally — a link revoked before finalization (D-044
    /// removal, or a cancellation) carries that terminal state onto the session unchanged, and only a
    /// link still Active at handover time renders the finalized notice.
    /// </summary>
    private static async Task<IResult> ResolveFinalizedNoticeAsync(
        Poll poll,
        ISessionRepository sessions,
        PollGuestLinkCredential credential,
        PollGuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionId sessionId = poll.CreatedSessionId!.Value;
        Session? session = await sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        GuestLinkView view;
        try
        {
            view = session.ResolveGuestLink(credential.GuestLinkId, clock);
        }
        catch (DomainException)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        if (view != GuestLinkView.LiveView)
        {
            ReissueTerminalCredential(context, protector, clock, credential);
            return Results.Ok(GuestPollResponse.Terminal(credential.PollId.Value, view));
        }

        return Results.Ok(GuestPollResponse.Finalized(credential.PollId.Value, sessionId.Value));
    }

    private static async Task<IResult> ClaimGuestPollLinkAsync(
        Guid guestLinkId,
        ClaimGuestPollRequest request,
        GuestPollUseCases useCase,
        GuestPollReadUseCases reads,
        IPollRepository polls,
        PollGuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await MutateAsync(
            guestLinkId,
            polls,
            reads,
            protector,
            clock,
            context,
            async (pollId, linkId, ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.DisplayName))
                {
                    throw new DomainException(DomainError.ValidationFailed, "A guest claims with a display name (D-017).");
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

                return await useCase.ClaimGuestLinkAsync(pollId, linkId, displayName, ct);
            },
            cancellationToken);

    private static async Task<IResult> VoteOnPollOptionAsGuestAsync(
        Guid guestLinkId,
        Guid optionId,
        VoteOnPollOptionRequest request,
        GuestPollUseCases useCase,
        GuestPollReadUseCases reads,
        IPollRepository polls,
        PollGuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await MutateAsync(
            guestLinkId,
            polls,
            reads,
            protector,
            clock,
            context,
            async (pollId, linkId, ct) =>
            {
                VoteChoice? choice = PollWireFormat.ParseVoteChoice(request.Choice);
                if (choice is null)
                {
                    throw new DomainException(DomainError.ValidationFailed, "Unknown vote choice.");
                }

                return await useCase.VoteAsGuestAsync(pollId, linkId, PollOptionId.From(optionId), choice.Value, ct);
            },
            cancellationToken);

    private static async Task<IResult> SuggestPollOptionAsGuestAsync(
        Guid guestLinkId,
        SuggestPollOptionRequest request,
        GuestPollUseCases useCase,
        GuestPollReadUseCases reads,
        IPollRepository polls,
        PollGuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await MutateAsync(
            guestLinkId,
            polls,
            reads,
            protector,
            clock,
            context,
            async (pollId, linkId, ct) =>
            {
                if (!PollWireFormat.TryParseInstant(request.StartTime, out Instant startTime))
                {
                    throw new DomainException(DomainError.ValidationFailed, "Malformed start time.");
                }

                return await useCase.SuggestOptionAsGuestAsync(pollId, linkId, startTime, request.DurationMinutes, ct);
            },
            cancellationToken);

    private static async Task<IResult> RenameGuestPollIdentityAsync(
        Guid guestLinkId,
        RenameGuestPollIdentityRequest request,
        GuestPollUseCases useCase,
        GuestPollReadUseCases reads,
        IPollRepository polls,
        PollGuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await MutateAsync(
            guestLinkId,
            polls,
            reads,
            protector,
            clock,
            context,
            async (pollId, linkId, ct) =>
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

                return await useCase.RenameGuestAsync(pollId, linkId, displayName, ct);
            },
            cancellationToken);

    /// <summary>
    /// D-225's poll arm: both credentials required (the route's <c>RequireOnboarded</c> policy and
    /// an Active-mode poll guest cookie), fail-closed 404 on a missing or stale one, and the guest
    /// cookie cleared on success because the link is now terminal.
    /// </summary>
    private static async Task<IResult> UpgradeGuestPollAsync(
        Guid guestLinkId,
        UpgradeGuestPollUseCase useCase,
        ICurrentUserAccessor currentUser,
        PollGuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = (await currentUser.GetAsync(cancellationToken))?.Id;
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        PollGuestLinkCredential? credential = ReadCredential(context, guestLinkId, protector, clock);
        if (credential is null || credential.Mode != GuestCredentialMode.Active)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        GuestPollCommandResult result;
        try
        {
            result = await useCase.UpgradeAsync(
                credential.PollId, credential.GuestLinkId, actorId.Value, cancellationToken);
        }
        catch (DomainException ex)
        {
            return ex.Error == DomainError.NotFound
                ? Problem(context, StatusCodes.Status404NotFound, "poll_not_found")
                : Problem(context, StatusCodes.Status409Conflict, "poll_terminal");
        }

        if (!result.Succeeded)
        {
            return Problem(context, UpgradeStatusFor(result.Code!), result.Code!);
        }

        ClearGuestCookie(context, credential.GuestLinkId);
        return Results.Ok(new GuestPollUpgradeResponse(credential.PollId.Value));
    }

    private static int UpgradeStatusFor(string code) => code switch
    {
        "guest_link_terminal" or "guest_link_unclaimed" or "upgrade_unavailable" =>
            StatusCodes.Status409Conflict,
        _ => StatusCodes.Status404NotFound,
    };

    /// <summary>D-225: the upgraded link is spent — drop the bearer cookie on the way out.</summary>
    private static void ClearGuestCookie(HttpContext context, GuestLinkId guestLinkId)
    {
        bool isDevelopment = context.RequestServices
            .GetRequiredService<IWebHostEnvironment>().IsDevelopment();
        string guestLinkIdN = guestLinkId.Value.ToString("N");
        context.Response.Cookies.Delete(
            $"pu.g.{guestLinkIdN}",
            new CookieOptions
            {
                HttpOnly = true,
                Secure = !isDevelopment,
                SameSite = SameSiteMode.Lax,
                Path = $"{ApiRoutes.V1}/guest/{guestLinkIdN}",
            });
    }

    /// <summary>
    /// The shared mutation path, mirroring <see cref="GuestSessionEndpoints"/>'s: resolve access
    /// first (never invoking the caller's command for a terminal or finalized link), then run it and
    /// re-render the fresh <see cref="GuestPollResponse"/> from a separate guest read.
    /// </summary>
    private static async Task<IResult> MutateAsync(
        Guid guestLinkId,
        IPollRepository polls,
        GuestPollReadUseCases reads,
        PollGuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        Func<PollId, GuestLinkId, CancellationToken, Task<GuestPollCommandResult>> mutate,
        CancellationToken cancellationToken)
    {
        PollGuestLinkCredential? credential = ReadCredential(context, guestLinkId, protector, clock);
        if (credential is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        Poll? poll = await polls.FindAsync(credential.PollId, cancellationToken);
        if (poll is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        if (poll.Status == PollStatus.Finalized)
        {
            return Problem(context, StatusCodes.Status409Conflict, "poll_terminal");
        }

        GuestLinkView view;
        try
        {
            view = poll.ResolveGuestLink(credential.GuestLinkId, clock);
        }
        catch (DomainException)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        if (view != GuestLinkView.LiveView)
        {
            return Problem(context, StatusCodes.Status409Conflict, "guest_link_terminal");
        }

        if (credential.Mode != GuestCredentialMode.Active)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        GuestPollCommandResult result;
        try
        {
            result = await mutate(credential.PollId, credential.GuestLinkId, cancellationToken);
        }
        catch (DomainException ex) when (ex.Error == DomainError.ValidationFailed)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "poll_validation");
        }

        if (!result.Succeeded)
        {
            return Problem(context, StatusFor(result.Code!), result.Code!);
        }

        GuestPollDetail? detail = await reads.GetGuestPollDetailAsync(credential.PollId, credential.GuestLinkId, cancellationToken);
        return detail is null
            ? Problem(context, StatusCodes.Status404NotFound, "poll_not_found")
            : Results.Ok(GuestPollResponse.Live(detail));
    }

    private static void ReissueTerminalCredential(
        HttpContext context,
        PollGuestLinkCredentialProtector protector,
        IClock clock,
        PollGuestLinkCredential credential)
    {
        Instant now = clock.GetCurrentInstant();
        var terminal = new PollGuestLinkCredential(
            credential.GuestLinkId,
            credential.PollId,
            GuestCredentialMode.Terminal,
            now,
            now + PollGuestLinkCredentialProtector.TerminalLifetime);

        SetPollGuestCookie(context, protector, terminal, PollGuestLinkCredentialProtector.TerminalLifetime);
    }

    private static PollGuestLinkCredential? ReadCredential(
        HttpContext context,
        Guid guestLinkId,
        PollGuestLinkCredentialProtector protector,
        IClock clock)
    {
        string cookieName = $"pu.g.{guestLinkId:N}";
        if (!context.Request.Cookies.TryGetValue(cookieName, out string? cookieValue) || cookieValue is null)
        {
            return null;
        }

        // Structurally fails on a session-typed cookie value — the poll protector's purpose cannot
        // decrypt what the session protector encrypted, so this is null exactly as often as a
        // genuinely missing credential.
        PollGuestLinkCredential? credential = protector.Unprotect(cookieValue);
        if (credential is null ||
            credential.GuestLinkId.Value != guestLinkId ||
            credential.IsExpired(clock))
        {
            return null;
        }

        return credential;
    }

    internal static void SetPollGuestCookie(
        HttpContext context,
        PollGuestLinkCredentialProtector protector,
        PollGuestLinkCredential credential,
        Duration lifetime)
    {
        bool isDevelopment = context.RequestServices
            .GetRequiredService<IWebHostEnvironment>().IsDevelopment();
        string guestLinkIdN = credential.GuestLinkId.Value.ToString("N");
        context.Response.Cookies.Append(
            $"pu.g.{guestLinkIdN}",
            protector.Protect(credential),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = !isDevelopment,
                SameSite = SameSiteMode.Lax,
                Path = $"{ApiRoutes.V1}/guest/{guestLinkIdN}",
                MaxAge = lifetime.ToTimeSpan() is { Ticks: > 0 } positive ? positive : TimeSpan.Zero,
            });
    }

    /// <summary>
    /// Mirrors <c>PollEndpoints.StatusToCodeStatus</c>, extended with the guest-only codes
    /// <see cref="GuestPollUseCases.RequireClaimedGuest"/>-shaped failures produce: the two are both
    /// fail-closed 404s (a missing or inactive link reads exactly like a missing poll), and the
    /// explicit vote-before-claim guard is the ticket's own 409.
    /// </summary>
    private static int StatusFor(string code) => code switch
    {
        "poll_not_found" or "poll_option_not_found" or "poll_guest_link_not_found" or "poll_guest_link_inactive" =>
            StatusCodes.Status404NotFound,
        "poll_option_cap" or "poll_voting_closed" or "poll_selection_window_closed" or "poll_zero_options" =>
            StatusCodes.Status422UnprocessableEntity,
        "poll_terminal" or "poll_invalid_transition" or "poll_conflict" or "guest_link_not_claimed" =>
            StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

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
