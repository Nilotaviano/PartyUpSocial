using System.Text.Json;
using NodaTime;
using NodaTime.Text;
using PartyUp.Api.Auth;
using PartyUp.Api.Contracts.Availability;
using PartyUp.Api.Contracts.Sessions;
using PartyUp.Api.ErrorHandling;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Availability;
using PartyUp.Application.Deletion;
using PartyUp.Application.Onboarding;
using PartyUp.Application.Profile;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Api.Endpoints;

public static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", GetMyProfileAsync)
            .WithName("GetMyProfile")
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces<MyProfileResponse>();
        api.MapPut("/me/username", ChangeUsernameAsync)
            .WithName("ChangeMyUsername")
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);
        api.MapPut("/me/display-name", ChangeDisplayNameAsync)
            .WithName("ChangeMyDisplayName")
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);
        api.MapPut("/me/avatar", ChangeAvatarAsync)
            .WithName("ChangeMyAvatar")
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);
        api.MapPut("/me/time-zone", ChangeTimeZoneAsync)
            .WithName("ChangeMyTimeZone")
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);
        api.MapPut("/me/locale", ChangeLocaleAsync)
            .WithName("ChangeMyLocale")
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);
        api.MapPut("/me/reminder-default", ChangeReminderDefaultAsync)
            .WithName("ChangeMyReminderDefault")
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);
        api.MapPost("/me/deletion", RequestDeletionAsync)
            .WithName(ApiOperationIds.DeleteMe)
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces<AccountDeletionResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict);

        RouteGroupBuilder links = api.MapGroup("/me/platform-links")
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated);

        links.MapGet(string.Empty, GetAsync)
            .WithName("GetPlatformLinks")
            .Produces<PlatformLinksResponse>();
        api.MapPost("/me/platform-links", LinkAsync)
            .WithName("LinkPlatform")
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces<PlatformLinkResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        links.MapPatch("/{id:guid}", ChangeHandleAsync)
            .WithName("ChangePlatformHandle")
            .Produces<PlatformLinkResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        links.MapDelete("/{id:guid}", UnlinkAsync)
            .WithName(ApiOperationIds.DeletePlatformLink)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // PHASE5_DESIGN.md §8: /me/availability/** and /me/actionable are mapped here — MeEndpoints is
        // the shipped owner of every /me/** route (§12 S5 row).
        RouteGroupBuilder availability = api.MapGroup("/me/availability")
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);

        availability.MapGet(string.Empty, GetMyAvailabilityAsync)
            .WithName(ApiOperationIds.GetMyAvailability)
            .Produces<MyAvailabilityLayerResponse>()
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest);

        availability.MapPost("/blocks", CreateAvailabilityBlockAsync)
            .WithName(ApiOperationIds.CreateAvailabilityBlock)
            .Produces<AvailabilityBlockView>(StatusCodes.Status201Created)
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest);

        availability.MapPatch("/blocks/{blockId:guid}", EditAvailabilityBlockAsync)
            .WithName(ApiOperationIds.EditAvailabilityBlock)
            .Produces<AvailabilityBlockView>()
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest)
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        // round-1 fix (F8): DeleteAvailabilityBlock/DeleteAvailabilitySeries never emit 404 — a
        // missing or non-owner target is the shipped idempotent 204 no-op (§5.2/§8) — so the
        // previously-declared 404 was unreachable by design; removed from both declarations, and the
        // §8-row deviation is recorded in TRACEABILITY.md's S5 section.
        availability.MapDelete("/blocks/{blockId:guid}", DeleteAvailabilityBlockAsync)
            .WithName(ApiOperationIds.DeleteAvailabilityBlock)
            .Produces(StatusCodes.Status204NoContent);

        availability.MapPost("/series", CreateAvailabilitySeriesAsync)
            .WithName(ApiOperationIds.CreateAvailabilitySeries)
            .Produces<AvailabilitySeriesView>(StatusCodes.Status201Created)
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest);

        availability.MapDelete("/series/{seriesId:guid}", DeleteAvailabilitySeriesAsync)
            .WithName(ApiOperationIds.DeleteAvailabilitySeries)
            .Produces(StatusCodes.Status204NoContent);

        availability.MapPatch("/series/{seriesId:guid}/occurrences/{occurrenceDate}", EditAvailabilityOccurrenceAsync)
            .WithName(ApiOperationIds.EditAvailabilityOccurrence)
            .Produces<EditOccurrenceResponse>()
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest)
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        availability.MapDelete(
                "/series/{seriesId:guid}/occurrences/{occurrenceDate}", DeleteAvailabilityOccurrenceAsync)
            .WithName(ApiOperationIds.DeleteAvailabilityOccurrence)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        api.MapGet("/me/actionable", GetActionableInboxAsync)
            .WithName(ApiOperationIds.GetActionableInbox)
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded)
            .Produces<ActionableInboxResponse>();

        return api;
    }

    private static async Task<IResult> GetMyProfileAsync(
        ICurrentUserAccessor currentUser,
        GetMyProfileUseCase useCase,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        MyProfileResult? result = await useCase.ExecuteAsync(caller.Id, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(MyProfileResponse.From(result));
    }

    private static async Task<IResult> ChangeUsernameAsync(
        ChangeUsernameRequest request,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        ChangeUsernameUseCase useCase,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        ProfileMutationResult result = await useCase.ExecuteAsync(
            caller.Id,
            request.Username,
            cancellationToken);
        if (result.Succeeded)
        {
            return Results.NoContent();
        }

        if (result.RetryAt is { } retryAt)
        {
            return Results.Json(
                new ProfileProblem(
                    "about:blank",
                    "Conflict",
                    StatusCodes.Status409Conflict,
                    result.Code!,
                    context.TraceIdentifier,
                    InstantPattern.ExtendedIso.Format(retryAt)),
                statusCode: StatusCodes.Status409Conflict,
                contentType: "application/problem+json");
        }

        return ProfileMutationProblem(context, result);
    }

    private static Task<IResult> ChangeDisplayNameAsync(
        ChangeDisplayNameRequest request,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        ChangeDisplayNameUseCase useCase,
        CancellationToken cancellationToken) =>
        ExecuteProfileMutationAsync(
            context,
            currentUser,
            (id, ct) => useCase.ExecuteAsync(id, request.DisplayName, ct),
            cancellationToken);

    private static Task<IResult> ChangeAvatarAsync(
        AvatarDraft request,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        ChangeAvatarUseCase useCase,
        CancellationToken cancellationToken) =>
        ExecuteProfileMutationAsync(
            context,
            currentUser,
            (id, ct) => useCase.ExecuteAsync(id, request, ct),
            cancellationToken);

    private static Task<IResult> ChangeTimeZoneAsync(
        ChangeTimeZoneRequest request,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        ChangeTimeZoneUseCase useCase,
        CancellationToken cancellationToken) =>
        ExecuteProfileMutationAsync(
            context,
            currentUser,
            (id, ct) => useCase.ExecuteAsync(id, request.TimeZone, ct),
            cancellationToken);

    private static Task<IResult> ChangeLocaleAsync(
        ChangeLocaleRequest request,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        ChangeLocaleUseCase useCase,
        CancellationToken cancellationToken) =>
        ExecuteProfileMutationAsync(
            context,
            currentUser,
            (id, ct) => useCase.ExecuteAsync(id, request.Locale, ct),
            cancellationToken);

    private static async Task<IResult> ChangeReminderDefaultAsync(
        ChangeReminderDefaultRequest request,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        SetReminderDefaultUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(request.ReminderDefault, true, out ReminderOffset reminder) ||
            !Enum.IsDefined(reminder))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_request");
        }

        return await ExecuteProfileMutationAsync(
            context,
            currentUser,
            (id, ct) => useCase.ExecuteAsync(id, reminder, ct),
            cancellationToken);
    }

    private static async Task<IResult> ExecuteProfileMutationAsync(
        HttpContext context,
        ICurrentUserAccessor currentUser,
        Func<UserId, CancellationToken, Task<ProfileMutationResult>> execute,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        ProfileMutationResult result = await execute(caller.Id, cancellationToken);
        return result.Succeeded ? Results.NoContent() : ProfileMutationProblem(context, result);
    }

    private static async Task<IResult> RequestDeletionAsync(
        HttpContext context,
        ICurrentUserAccessor currentUser,
        ReauthenticateUseCase reauthenticate,
        RequestAccountDeletionUseCase useCase,
        CancellationToken cancellationToken)
    {
        IResult? malformedBody = await AuthEndpoints.ValidateOptionalJsonBodyAsync(
            context,
            cancellationToken);
        if (malformedBody is not null)
        {
            return malformedBody;
        }

        AuthMethodProofRequest? proof = await AuthEndpoints.ReadOptionalBodyAsync<AuthMethodProofRequest>(
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
        AccountDeletionRequestResult result = await useCase.ExecuteAsync(
            new RequestAccountDeletionCommand(caller.Id, proven),
            cancellationToken);
        return result.Succeeded
            ? Results.Accepted(value: new AccountDeletionResponse(
                InstantPattern.ExtendedIso.Format(result.GraceEndsAt!.Value)))
            : Problem(context, StatusCodes.Status409Conflict, result.Code!);
    }

    // ---- PHASE5_DESIGN.md §8: availability, calendar-adjacent /me routes -----------------------

    private static async Task<IResult> GetMyAvailabilityAsync(
        string? from,
        string? to,
        ICurrentUserAccessor currentUser,
        ReadMyAvailabilityUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        if (!TryParseInstantRange(from, to, out TimeRange range))
        {
            return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "invalid_range");
        }

        MyAvailabilityLayer layer;
        try
        {
            layer = await useCase.ExecuteAsync(actorId.Value, range, cancellationToken);
        }
        catch (DomainException ex) when (ex.Error == DomainError.RangeTooLarge)
        {
            return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "range_too_large");
        }

        return Results.Ok(MyAvailabilityLayerResponse.From(layer, includeRange: true));
    }

    private static async Task<IResult> CreateAvailabilityBlockAsync(
        CreateAvailabilityBlockRequest request,
        ICurrentUserAccessor currentUser,
        CreateAvailabilityBlockUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        if (!TryParseInstantRange(request.StartTime, request.EndTime, out TimeRange range))
        {
            return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
        }

        AvailabilityBlock block;
        try
        {
            block = await useCase.ExecuteAsync(actorId.Value, range, cancellationToken);
        }
        catch (DomainException ex) when (ex.Error == DomainError.ValidationFailed)
        {
            return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
        }

        return Results.Created(
            $"{ApiRoutes.V1}/me/availability/blocks/{block.Id.Value:D}",
            AvailabilityBlockView.From(block));
    }

    private static async Task<IResult> EditAvailabilityBlockAsync(
        Guid blockId,
        EditAvailabilityBlockRequest request,
        ICurrentUserAccessor currentUser,
        EditAvailabilityBlockUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        if (!TryParseInstantRange(request.StartTime, request.EndTime, out TimeRange range))
        {
            return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
        }

        AvailabilityBlock block;
        try
        {
            block = await useCase.ExecuteAsync(
                actorId.Value, AvailabilityBlockId.From(blockId), range, cancellationToken);
        }
        catch (DomainException ex) when (ex.Error == DomainError.NotFound)
        {
            return AvailabilityProblem(context, StatusCodes.Status404NotFound, "availability_not_found");
        }
        catch (DomainException ex) when (ex.Error == DomainError.ValidationFailed)
        {
            return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
        }

        return Results.Ok(AvailabilityBlockView.From(block));
    }

    private static async Task<IResult> DeleteAvailabilityBlockAsync(
        Guid blockId,
        ICurrentUserAccessor currentUser,
        DeleteAvailabilityBlockUseCase useCase,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        // Idempotent (§8): a missing or non-owner target is a no-op, never an error.
        await useCase.ExecuteAsync(actorId.Value, AvailabilityBlockId.From(blockId), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateAvailabilitySeriesAsync(
        CreateAvailabilitySeriesRequest request,
        ICurrentUserAccessor currentUser,
        IUserRepository users,
        CreateAvailabilitySeriesUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        User? user = await users.FindAsync(actorId.Value, cancellationToken);
        if (user?.TimeZone is not { } zone)
        {
            return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
        }

        if (request.DayOfWeek is not { } dayOfWeekValue ||
            dayOfWeekValue is < 1 or > 7 ||
            !AvailabilityWireFormat.TryParseTime(request.StartTime, out LocalTime startTime) ||
            request.DurationMinutes is not { } durationMinutes ||
            !AvailabilityWireFormat.TryParseDate(request.EffectiveFrom, out LocalDate effectiveFrom))
        {
            return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
        }

        LocalDate? effectiveUntil = null;
        if (request.EffectiveUntil is not null)
        {
            if (!AvailabilityWireFormat.TryParseDate(request.EffectiveUntil, out LocalDate until))
            {
                return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
            }

            effectiveUntil = until;
        }

        AvailabilitySeries series;
        try
        {
            WeeklyPattern pattern = WeeklyPattern.Create(
                (IsoDayOfWeek)dayOfWeekValue, startTime, Duration15.FromMinutes(durationMinutes));
            series = await useCase.ExecuteAsync(
                actorId.Value, pattern, zone, effectiveFrom, effectiveUntil, cancellationToken);
        }
        catch (DomainException ex) when (ex.Error == DomainError.ValidationFailed)
        {
            return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
        }

        return Results.Created(
            $"{ApiRoutes.V1}/me/availability/series/{series.Id.Value:D}",
            AvailabilitySeriesView.From(series));
    }

    private static async Task<IResult> DeleteAvailabilitySeriesAsync(
        Guid seriesId,
        ICurrentUserAccessor currentUser,
        DeleteAvailabilitySeriesUseCase useCase,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        // Idempotent, cascades to skips and materialized blocks (§12-21).
        await useCase.ExecuteAsync(actorId.Value, AvailabilitySeriesId.From(seriesId), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> EditAvailabilityOccurrenceAsync(
        Guid seriesId,
        string occurrenceDate,
        EditOccurrenceRequest request,
        ICurrentUserAccessor currentUser,
        EditAvailabilityOccurrenceUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        // A date that fails to parse takes the same 404 as a date outside [EffectiveFrom,
        // EffectiveUntil) or off the series weekday — indistinguishability is the visibility-safe
        // default (§8).
        if (!AvailabilityWireFormat.TryParseDate(occurrenceDate, out LocalDate date))
        {
            return AvailabilityProblem(context, StatusCodes.Status404NotFound, "occurrence_not_found");
        }

        OccurrenceEditScope scope;
        TimeRange? onlyThisRange = null;
        WeeklyPattern? thisAndFuturePattern = null;

        switch (request.Scope)
        {
            case "onlyThis":
                // round-1 fix (F7): the union is closed per-arm — thisAndFuture-only fields on an
                // onlyThis body are rejected, not silently ignored.
                if (request.DayOfWeek is not null || request.DurationMinutes is not null)
                {
                    return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
                }

                scope = OccurrenceEditScope.OnlyThis;
                if (!TryParseInstantRange(request.StartTime, request.EndTime, out TimeRange range))
                {
                    return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
                }

                onlyThisRange = range;
                break;
            case "thisAndFuture":
                // round-1 fix (F7): onlyThis-only field on a thisAndFuture body is rejected.
                if (request.EndTime is not null)
                {
                    return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
                }

                scope = OccurrenceEditScope.ThisAndFuture;
                if (request.DayOfWeek is not { } dayOfWeekValue ||
                    dayOfWeekValue is < 1 or > 7 ||
                    !AvailabilityWireFormat.TryParseTime(request.StartTime, out LocalTime startTime) ||
                    request.DurationMinutes is not { } durationMinutes)
                {
                    return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
                }

                try
                {
                    thisAndFuturePattern = WeeklyPattern.Create(
                        (IsoDayOfWeek)dayOfWeekValue, startTime, Duration15.FromMinutes(durationMinutes));
                }
                catch (DomainException)
                {
                    return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
                }

                break;
            default:
                return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
        }

        OccurrenceEditOutcome outcome;
        try
        {
            outcome = await useCase.ExecuteAsync(
                actorId.Value,
                AvailabilitySeriesId.From(seriesId),
                date,
                scope,
                onlyThisRange,
                thisAndFuturePattern,
                cancellationToken);
        }
        catch (DomainException ex) when (ex.Error == DomainError.NotFound)
        {
            // §8: series missing/not owned, occurrence outside range, and off-weekday all collapse
            // to the same occurrence_not_found 404 — no new DomainError member is added.
            return AvailabilityProblem(context, StatusCodes.Status404NotFound, "occurrence_not_found");
        }
        catch (DomainException ex) when (ex.Error == DomainError.ValidationFailed)
        {
            return AvailabilityProblem(context, StatusCodes.Status400BadRequest, "availability_validation");
        }

        // §8.1: the closed discriminated response — onlyThis carries only {scope, block};
        // thisAndFuture carries {scope, original (nullable), successor}. round-1 fix (F1): a real
        // typed union (EditOccurrenceResponse) replaces the prior untyped anonymous objects, so the
        // OpenAPI document and the generated client both carry a real schema instead of `unknown`.
        EditOccurrenceResponse response = outcome.Scope == OccurrenceEditScope.OnlyThis
            ? new EditOccurrenceResponse.OnlyThis(AvailabilityBlockView.From(outcome.Block!))
            : new EditOccurrenceResponse.ThisAndFuture(
                outcome.Original is { } original ? AvailabilitySeriesView.From(original) : null,
                AvailabilitySeriesView.From(outcome.Successor!));
        return Results.Ok(response);
    }

    private static async Task<IResult> DeleteAvailabilityOccurrenceAsync(
        Guid seriesId,
        string occurrenceDate,
        ICurrentUserAccessor currentUser,
        DeleteAvailabilityOccurrenceUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        if (!AvailabilityWireFormat.TryParseDate(occurrenceDate, out LocalDate date))
        {
            return AvailabilityProblem(context, StatusCodes.Status404NotFound, "occurrence_not_found");
        }

        try
        {
            // Idempotent via the skip PK (§8); on a date already edited via onlyThis, also removes
            // that edit's materialized block.
            await useCase.ExecuteAsync(actorId.Value, AvailabilitySeriesId.From(seriesId), date, cancellationToken);
        }
        catch (DomainException ex) when (ex.Error == DomainError.NotFound)
        {
            return AvailabilityProblem(context, StatusCodes.Status404NotFound, "occurrence_not_found");
        }

        return Results.NoContent();
    }

    private static async Task<IResult> GetActionableInboxAsync(
        ICurrentUserAccessor currentUser,
        GetActionableInboxUseCase useCase,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        ActionableInbox inbox = await useCase.ExecuteAsync(actorId.Value, cancellationToken);
        return Results.Ok(ActionableInboxResponse.From(inbox, new MaskedParticipantAssigner()));
    }

    /// <summary>Both endpoints are Instant strings — used by GET /me/availability and block create/edit.</summary>
    private static bool TryParseInstantRange(string? startTime, string? endTime, out TimeRange range)
    {
        range = default;
        if (!AvailabilityWireFormat.TryParseInstant(startTime, out Instant start) ||
            !AvailabilityWireFormat.TryParseInstant(endTime, out Instant end))
        {
            return false;
        }

        try
        {
            range = new TimeRange(start, end);
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    private static IResult AvailabilityProblem(HttpContext context, int status, string code) =>
        PartyUpProblemResults.Create(context, status, code);

    private static async Task<UserId?> ActorIdAsync(
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken) =>
        (await currentUser.GetAsync(cancellationToken))?.Id;

    private static IResult ProfileMutationProblem(
        HttpContext context,
        ProfileMutationResult result)
    {
        int status = result.Code == "not_found"
            ? StatusCodes.Status404NotFound
            : result.Code == "invalid_time_zone" ||
                result.Code is not null && result.Code.StartsWith("errors.", StringComparison.Ordinal)
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status409Conflict;
        return Problem(context, status, result.Code!);
    }

    private static async Task<IResult> GetAsync(
        ICurrentUserAccessor currentUser,
        IUserRepository users,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        User? user = await users.FindAsync(caller.Id, cancellationToken);
        return user is null
            ? Results.NotFound()
            : Results.Ok(new PlatformLinksResponse(user.PlatformLinks.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> LinkAsync(
        LinkPlatformRequest request,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        LinkPlatformUseCase useCase,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        if (!Enum.TryParse(request.Platform, true, out Platform platform) ||
            !Enum.IsDefined(platform) ||
            platform == Platform.NotApplicable)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_request");
        }

        PlatformLinkMutationResult result = await useCase.ExecuteAsync(
            new LinkPlatformCommand(caller.Id, platform, request.Handle),
            cancellationToken);
        if (!result.Succeeded)
        {
            int status = result.Code switch
            {
                "not_found" => StatusCodes.Status404NotFound,
                string code when code.StartsWith("errors.text.", StringComparison.Ordinal) =>
                    StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status409Conflict,
            };
            return Problem(context, status, result.Code!);
        }

        PlatformLinkResponse response = ToResponse(result.Link!);
        return Results.Created($"{context.Request.Path}/{response.Id:D}", response);
    }

    private static async Task<IResult> ChangeHandleAsync(
        Guid id,
        ChangePlatformHandleRequest request,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        ChangePlatformHandleUseCase useCase,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        PlatformLinkMutationResult result = await useCase.ExecuteAsync(
            new ChangePlatformHandleCommand(caller.Id, PlatformLinkId.From(id), request.Handle),
            cancellationToken);
        return result.Succeeded
            ? Results.Ok(ToResponse(result.Link!))
            : Problem(
                context,
                result.Code == "not_found" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest,
                result.Code!);
    }

    private static async Task<IResult> UnlinkAsync(
        Guid id,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        IUserRepository users,
        UnlinkPlatformUseCase unlinkPlatform,
        UnlinkSteamUseCase unlinkSteam,
        ReauthenticateUseCase reauthenticate,
        CancellationToken cancellationToken)
    {
        UnlinkPlatformProofRequest? proof;
        try
        {
            proof = await ReadOptionalProofAsync(context, cancellationToken);
        }
        catch (JsonException)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_request");
        }

        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        PlatformLinkId linkId = PlatformLinkId.From(id);
        User? user = await users.FindAsync(caller.Id, cancellationToken);
        PlatformLink? link = user?.PlatformLinks.SingleOrDefault(value => value.Id == linkId);
        if (link is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, "not_found");
        }

        if (link.Platform != Platform.Steam)
        {
            UnlinkPlatformResult platformResult = await unlinkPlatform.ExecuteAsync(
                caller.Id,
                linkId,
                cancellationToken);
            return platformResult == UnlinkPlatformResult.Removed
                ? Results.NoContent()
                : Problem(context, StatusCodes.Status404NotFound, "not_found");
        }

        bool proven = await reauthenticate.ValidateProofAsync(
            caller.Id,
            proof?.CurrentPassword,
            proof?.ReauthToken,
            cancellationToken);
        UnlinkSteamResult result = await unlinkSteam.ExecuteAsync(
            new UnlinkSteamCommand(
                caller.Id,
                UnlinkOrigin.PlatformLink,
                proven,
                linkId),
            cancellationToken);
        if (result.Succeeded)
        {
            return Results.NoContent();
        }

        int status = result.Code == "not_found"
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status409Conflict;
        return Problem(context, status, result.Code!);
    }

    private static async Task<UnlinkPlatformProofRequest?> ReadOptionalProofAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            return null;
        }

        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, cancellationToken);
        if (body.Length == 0)
        {
            return null;
        }

        body.Position = 0;
        return await JsonSerializer.DeserializeAsync<UnlinkPlatformProofRequest>(
            body,
            JsonSerializerOptions.Web,
            cancellationToken: cancellationToken);
    }

    private static PlatformLinkResponse ToResponse(PlatformLink link) =>
        new(link.Id.Value, link.Platform.ToString(), link.Handle.Value);

    private static IResult Problem(HttpContext context, int status, string code) =>
        Results.Json(
            new AuthProblem(
                "about:blank",
                status switch
                {
                    StatusCodes.Status404NotFound => "Not Found",
                    StatusCodes.Status409Conflict => "Conflict",
                    _ => "Bad Request",
                },
                status,
                code,
                code == "reauth_required" ? null : context.TraceIdentifier),
            statusCode: status,
            contentType: "application/problem+json");
}

public sealed record LinkPlatformRequest(string Platform, string Handle);

public sealed record ChangePlatformHandleRequest(string Handle);

public sealed record UnlinkPlatformProofRequest(string? CurrentPassword, string? ReauthToken);

public sealed record PlatformLinkResponse(Guid Id, string Platform, string Handle);

public sealed record PlatformLinksResponse(IReadOnlyList<PlatformLinkResponse> Links);

public sealed record ChangeUsernameRequest(string Username);

public sealed record ChangeDisplayNameRequest(string DisplayName);

public sealed record ChangeTimeZoneRequest(string TimeZone);

public sealed record ChangeLocaleRequest(string Locale);

public sealed record ChangeReminderDefaultRequest(string ReminderDefault);

public sealed record AccountDeletionResponse(string GraceEndsAt);

public sealed record ProfileProblem(
    string Type,
    string Title,
    int Status,
    string Code,
    string TraceId,
    string RetryAt);

public sealed record MyProfileResponse(
    string? Username,
    string? DisplayName,
    AvatarResponse? Avatar,
    string? TimeZone,
    string? Locale,
    string? FriendCode,
    string ReminderDefault,
    IReadOnlyList<PlatformLinkResponse> PlatformLinks,
    IReadOnlyList<AuthMethodResponse> AuthMethods)
{
    public static MyProfileResponse From(MyProfileResult profile)
    {
        User user = profile.User;
        return new MyProfileResponse(
            user.Username?.Value,
            user.DisplayName?.Value,
            user.Avatar is { } avatar ? AvatarResponse.From(avatar) : null,
            user.TimeZone?.Id,
            user.Locale?.Value,
            user.FriendCode?.Value,
            user.ReminderDefault.ToString(),
            user.PlatformLinks.Select(link => new PlatformLinkResponse(
                link.Id.Value,
                link.Platform.ToString(),
                link.Handle.Value)).ToArray(),
            profile.AuthMethods.Select(method => new AuthMethodResponse(
                method.Kind,
                method.Linked,
                method.Removable,
                method.Detail)).ToArray());
    }
}
