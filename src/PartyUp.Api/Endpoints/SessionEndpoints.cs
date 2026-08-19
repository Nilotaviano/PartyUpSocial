using System.Buffers.Text;
using NodaTime;
using NodaTime.Text;
using PartyUp.Api.Auth;
using PartyUp.Api.Contracts.Availability;
using PartyUp.Api.Contracts.Sessions;
using PartyUp.Api.ErrorHandling;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Notifications;
using PartyUp.Application.Sessions;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// The registered-user session HTTP surface (PHASE4_DESIGN.md §8). Every route sits under
/// <c>/api/v1</c>, requires <c>RequireOnboarded</c>, and inherits the group's
/// <c>ProblemAntiforgeryFilter</c> on unsafe methods (Program.cs). Rate limiting is by A-073
/// dimension (<c>session-write</c>/<c>session-attach</c>), applied by the global chained limiter —
/// no per-route policy attribute is needed.
/// </summary>
public static class SessionEndpoints
{
    private const int PageSize = 20;

    public static RouteGroupBuilder MapSessionEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder sessions = api.MapGroup(string.Empty)
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);

        BodyOperation(sessions.MapPost("/sessions", CreateSessionAsync)
                .WithName(ApiOperationIds.CreateSession)
                .Produces<CreateSessionResponse>(StatusCodes.Status201Created))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Common(sessions.MapGet("/sessions", ListSessionsAsync)
                .WithName(ApiOperationIds.ListSessions)
                .Produces<SessionPageResponse<SessionSummaryResponse>>());

        // Ahead of the {sessionId:guid} route only for readability — the guid constraint already
        // keeps the two from colliding.
        Common(sessions.MapGet("/sessions/history/stats", GetSessionHistoryStatsAsync)
                .WithName(ApiOperationIds.GetSessionHistoryStats)
                .Produces<SessionHistoryStatsResponse>());

        Common(sessions.MapGet("/sessions/{sessionId:guid}", GetSessionDetailAsync)
                .WithName(ApiOperationIds.GetSessionDetail)
                .Produces<SessionDetailResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        BodyOperation(sessions.MapPatch("/sessions/{sessionId:guid}", EditSessionAsync)
                .WithName(ApiOperationIds.EditSession)
                .Produces<SessionDetailResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(sessions.MapDelete("/sessions/{sessionId:guid}", CancelSessionAsync)
                .WithName(ApiOperationIds.CancelSession)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        BodyOperation(sessions.MapPost("/sessions/{sessionId:guid}/invites", InviteSessionUsersAsync)
                .WithName(ApiOperationIds.InviteSessionUsers)
                .Produces<SessionDetailResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        BodyOperation(sessions.MapPost("/sessions/{sessionId:guid}/guest-links", CreateSessionGuestLinksAsync)
                .WithName(ApiOperationIds.CreateSessionGuestLinks)
                .Produces<IReadOnlyList<GuestLinkInvitationResponse>>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(sessions.MapDelete(
                    "/sessions/{sessionId:guid}/guest-links/{guestLinkId:guid}",
                    RevokeSessionGuestLinkAsync)
                .WithName(ApiOperationIds.RevokeSessionGuestLink)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(sessions.MapDelete(
                    "/sessions/{sessionId:guid}/participations/{participationId:guid}/guest",
                    RemoveSessionGuestAsync)
                .WithName(ApiOperationIds.RemoveSessionGuest)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        BodyOperation(sessions.MapPut("/sessions/{sessionId:guid}/my-participation", RespondToSessionInvitationAsync)
                .WithName(ApiOperationIds.RespondToSessionInvitation)
                .Produces<ParticipationViewResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(sessions.MapDelete("/sessions/{sessionId:guid}/my-participation", LeaveSessionAsync)
                .WithName(ApiOperationIds.LeaveSession)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(sessions.MapPost("/sessions/{sessionId:guid}/join-requests", RequestSessionJoinAsync)
                .WithName(ApiOperationIds.RequestSessionJoin)
                .Produces<JoinRequestViewResponse>(StatusCodes.Status201Created))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(sessions.MapDelete("/sessions/{sessionId:guid}/join-requests/mine", WithdrawSessionJoinRequestAsync)
                .WithName(ApiOperationIds.WithdrawSessionJoinRequest)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Common(sessions.MapGet("/sessions/{sessionId:guid}/join-requests", ListSessionJoinRequestsAsync)
                .WithName(ApiOperationIds.ListSessionJoinRequests)
                .Produces<SessionPageResponse<JoinRequestViewResponse>>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(sessions.MapPost(
                    "/sessions/{sessionId:guid}/join-requests/{requestId:guid}/approve",
                    ApproveSessionJoinRequestAsync)
                .WithName(ApiOperationIds.ApproveSessionJoinRequest)
                .Produces<ParticipationViewResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(sessions.MapPost(
                    "/sessions/{sessionId:guid}/join-requests/{requestId:guid}/reject",
                    RejectSessionJoinRequestAsync)
                .WithName(ApiOperationIds.RejectSessionJoinRequest)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        Unsafe(sessions.MapPost(
                    "/sessions/{sessionId:guid}/participations/{participationId:guid}/promote",
                    PromoteSessionWaitlistParticipantAsync)
                .WithName(ApiOperationIds.PromoteSessionWaitlistParticipant)
                .Produces<ParticipationViewResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(sessions.MapPost(
                    "/sessions/{sessionId:guid}/participations/{participationId:guid}/reject",
                    RejectSessionWaitlistParticipantAsync)
                .WithName(ApiOperationIds.RejectSessionWaitlistParticipant)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        BodyOperation(sessions.MapPut("/sessions/{sessionId:guid}/my-reminder", SetSessionReminderAsync)
                .WithName(ApiOperationIds.SetSessionReminder)
                .Produces<ReminderViewResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        // PHASE7_DESIGN.md §4: MuteSession/UnmuteSession (D-090/D-113). Who may mute = anyone the
        // visibility engine grants access to; no access ⇒ 404, the shipped session_not_found code.
        Unsafe(sessions.MapPut("/sessions/{sessionId:guid}/mute", MuteSessionAsync)
                .WithName(ApiOperationIds.MuteSession)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(sessions.MapDelete("/sessions/{sessionId:guid}/mute", UnmuteSessionAsync)
                .WithName(ApiOperationIds.UnmuteSession)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static async Task<IResult> MuteSessionAsync(
        Guid sessionId,
        ICurrentUserAccessor currentUser,
        SetNotificationMuteUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        MuteResult result = await useCase.MuteSessionAsync(
            SessionId.From(sessionId), actorId.Value, cancellationToken);
        return result.Succeeded
            ? Results.NoContent()
            : Problem(context, StatusCodes.Status404NotFound, result.Code!);
    }

    private static async Task<IResult> UnmuteSessionAsync(
        Guid sessionId,
        ICurrentUserAccessor currentUser,
        SetNotificationMuteUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        MuteResult result = await useCase.UnmuteSessionAsync(
            SessionId.From(sessionId), actorId.Value, cancellationToken);
        return result.Succeeded
            ? Results.NoContent()
            : Problem(context, StatusCodes.Status404NotFound, result.Code!);
    }

    // ---- handlers ---------------------------------------------------------------------------

    private static async Task<IResult> CreateSessionAsync(
        CreateSessionRequest request,
        ICurrentUserAccessor currentUser,
        CreateSessionUseCase useCase,
        SessionReadUseCases reads,
        ISessionVersionStamp versionStamp,
        IConfiguration configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (!TryParseInstant(request.StartTime, out Instant startTime))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "session_validation");
        }

        Domain.Kernel.Platform? platform;
        SessionGame? game;
        ContentVisibility? visibility;
        try
        {
            platform = SessionWireFormat.ParsePlatform(request.Platform);
            game = request.Game is { } gameRequest ? gameRequest.ToDomain() : null;
            visibility = SessionWireFormat.ParseVisibility(request.Visibility);
            if (request.Game is not null && game is null)
            {
                return Problem(context, StatusCodes.Status400BadRequest, "session_validation");
            }
        }
        catch (FormatException)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "session_validation");
        }

        List<UserId> invitees = (request.InviteeUserIds ?? []).Select(UserId.From).ToList();
        List<FriendGroupId> audienceGroupIds = (request.AudienceGroupIds ?? []).Select(FriendGroupId.From).ToList();

        CreateSessionCommand command = new(
            actorId.Value,
            request.Title ?? string.Empty,
            startTime,
            request.DurationMinutes,
            platform,
            game,
            request.Notes,
            request.MinAttendance,
            request.MaxCapacity,
            visibility,
            audienceGroupIds,
            invitees,
            request.GuestLinkCount);

        CreateSessionResult result;
        try
        {
            result = await useCase.ExecuteAsync(command, cancellationToken);
        }
        catch (DomainException)
        {
            // D-129/D-130/D-142/D-099: the aggregate's own construction guards are all closed under
            // one code — the request never discloses which invariant tripped beyond "validation".
            return Problem(context, StatusCodes.Status400BadRequest, "session_validation");
        }

        if (!result.Succeeded)
        {
            int status = result.Code is "user_not_found" or "group_not_found"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest;
            return Problem(context, status, result.Code!);
        }

        SessionDetail sessionDetail = (await reads.GetSessionDetailAsync(
            result.Session!.Id, actorId.Value, cancellationToken))!;
        string detailVersion = versionStamp.Issue(sessionDetail.Id, sessionDetail.ConcurrencyToken);
        SessionDetailResponse detail = SessionDetailResponse.From(sessionDetail, detailVersion);
        var guestLinks = result.GuestLinks!
            .Select(link => new GuestLinkInvitationResponse(
                link.GuestLinkId.Value,
                BuildGuestUrl(configuration, link.Token)))
            .ToArray();

        return Results.Created(
            $"{ApiRoutes.V1}/sessions/{result.Session.Id.Value}",
            new CreateSessionResponse(detail, guestLinks));
    }

    private static async Task<IResult> ListSessionsAsync(
        string? scope,
        string? cursor,
        ICurrentUserAccessor currentUser,
        SessionReadUseCases useCase,
        ListDiscoverableSessionsUseCase discover,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (scope is not ("upcoming" or "history" or "discover"))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_scope");
        }

        var ordinals = new MaskedParticipantAssigner();

        if (scope == "discover")
        {
            // PHASE5_DESIGN.md A-077 (f): scope="discover", sortKey start_time ascending then id,
            // page 20. round-1 fix (F5): Discover is now a named SessionListScope member —
            // SessionReadRepository.ListDiscoverAsync mints its own page's cursor at that scope
            // directly, so a discover cursor is decoded/rejected exactly like every other list's,
            // never via an Unbound sentinel.
            if (!TryDecodeCursor(cursor, SessionListScope.Discover, out SessionListCursor? discoverCursor))
            {
                return Problem(context, StatusCodes.Status400BadRequest, "invalid_cursor");
            }

            SessionPage<SessionSummary> discoverPage = await discover.ExecuteAsync(
                actorId.Value, discoverCursor, PageSize, cancellationToken);
            return Results.Ok(SessionPageResponse.From(discoverPage, s => SessionSummaryResponse.From(s, ordinals)));
        }

        SessionListScope listScope = scope == "upcoming" ? SessionListScope.Upcoming : SessionListScope.History;

        if (!TryDecodeCursor(cursor, listScope, out SessionListCursor? decodedCursor))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_cursor");
        }

        SessionPage<SessionSummary> page = scope == "upcoming"
            ? await useCase.ListUpcomingSessionsAsync(actorId.Value, decodedCursor, PageSize, cancellationToken)
            : await useCase.ListCompletedHistoryAsync(actorId.Value, decodedCursor, PageSize, cancellationToken);

        page = RebindNextCursor(page, listScope);

        return Results.Ok(SessionPageResponse.From(page, s => SessionSummaryResponse.From(s, ordinals)));
    }

    /// <summary>
    /// D-222: the recap over the viewer's own completed history. One shared assigner, exactly like
    /// every other person-bearing response — a top mate seen twice keeps one ordinal.
    /// </summary>
    private static async Task<IResult> GetSessionHistoryStatsAsync(
        ICurrentUserAccessor currentUser,
        SessionReadUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        SessionHistoryStats stats = await useCase.GetHistoryStatsAsync(actorId.Value, cancellationToken);
        return Results.Ok(SessionHistoryStatsResponse.From(stats, new MaskedParticipantAssigner()));
    }

    private static async Task<IResult> GetSessionDetailAsync(
        Guid sessionId,
        ICurrentUserAccessor currentUser,
        SessionReadUseCases useCase,
        ISessionVersionStamp versionStamp,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        SessionDetail? detail = await useCase.GetSessionDetailAsync(
            SessionId.From(sessionId), actorId.Value, cancellationToken);
        if (detail is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, "session_not_found");
        }

        string version = versionStamp.Issue(detail.Id, detail.ConcurrencyToken);
        return Results.Ok(SessionDetailResponse.From(detail, version));
    }

    private static async Task<IResult> EditSessionAsync(
        Guid sessionId,
        EditSessionRequest request,
        ICurrentUserAccessor currentUser,
        SessionReadUseCases reads,
        EditSessionUseCase useCase,
        ISessionVersionStamp versionStamp,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        var id = SessionId.From(sessionId);

        if (!TryDecodeVersionToken(request.IfMatch))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_if_match");
        }

        Instant? startTime = null;
        if (request.StartTime is not null)
        {
            if (!TryParseInstant(request.StartTime, out Instant parsed))
            {
                return Problem(context, StatusCodes.Status400BadRequest, "session_validation");
            }

            startTime = parsed;
        }

        Domain.Kernel.Platform? platform;
        SessionGame? game;
        ContentVisibility? visibility;
        try
        {
            platform = SessionWireFormat.ParsePlatform(request.Platform);
            game = request.Game is { } gameRequest ? gameRequest.ToDomain() : null;
            visibility = SessionWireFormat.ParseVisibility(request.Visibility);
            if (request.Game is not null && game is null)
            {
                return Problem(context, StatusCodes.Status400BadRequest, "session_validation");
            }
        }
        catch (FormatException)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "session_validation");
        }

        IReadOnlyCollection<SessionEditClearField> clear = (request.Clear ?? [])
            .Select(field => field switch
            {
                ClearFieldRequest.MaxCapacity => SessionEditClearField.MaxCapacity,
                ClearFieldRequest.Notes => SessionEditClearField.Notes,
                ClearFieldRequest.Game => SessionEditClearField.Game,
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            })
            .ToArray();

        IReadOnlyCollection<FriendGroupId>? audienceGroupIds = request.AudienceGroupIds?
            .Select(FriendGroupId.From)
            .ToArray();

        EditSessionCommand command = new(
            id,
            actorId.Value,
            request.Title,
            startTime,
            request.DurationMinutes,
            platform,
            game,
            request.Notes,
            request.MinAttendance,
            request.MaxCapacity,
            visibility,
            audienceGroupIds,
            clear,
            request.IfMatch);

        EditSessionResult result;
        try
        {
            result = await useCase.ExecuteAsync(command, cancellationToken);
        }
        catch (DomainException ex)
        {
            return DomainProblem(context, ex, MapEditError);
        }

        if (!result.Succeeded)
        {
            int status = result.Code switch
            {
                "session_not_found" or "group_not_found" => StatusCodes.Status404NotFound,
                "session_conflict" => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            return Problem(context, status, result.Code!);
        }

        SessionDetail refreshed = (await reads.GetSessionDetailAsync(id, actorId.Value, cancellationToken))!;
        string version = versionStamp.Issue(id, refreshed.ConcurrencyToken);
        return Results.Ok(SessionDetailResponse.From(refreshed, version));
    }

    private static async Task<IResult> CancelSessionAsync(
        Guid sessionId,
        ICurrentUserAccessor currentUser,
        EditSessionUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        EditSessionResult result;
        try
        {
            result = await useCase.CancelAsync(SessionId.From(sessionId), actorId.Value, cancellationToken);
        }
        catch (DomainException ex) when (ex.Error == DomainError.InvalidTransition)
        {
            // D-156/D-157: only an Expired session's Cancel row rejects with 409 session_terminal.
            return Problem(context, StatusCodes.Status409Conflict, "session_terminal");
        }

        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status404NotFound, result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> InviteSessionUsersAsync(
        Guid sessionId,
        InviteSessionUsersRequest request,
        ICurrentUserAccessor currentUser,
        SessionInvitationUseCases useCase,
        SessionReadUseCases reads,
        ISessionVersionStamp versionStamp,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        var id = SessionId.From(sessionId);
        SessionCommandResult result;
        try
        {
            result = await useCase.InviteRegisteredUsersAsync(
                id,
                actorId.Value,
                request.UserIds.Select(UserId.From).ToArray(),
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return DomainProblem(context, ex, error => error switch
            {
                DomainError.WindowClosed or DomainError.TerminalState =>
                    (StatusCodes.Status422UnprocessableEntity, "session_invites_closed"),
                DomainError.Blocked => (StatusCodes.Status404NotFound, "user_not_found"),
                _ => (StatusCodes.Status400BadRequest, "session_validation"),
            });
        }

        if (!result.Succeeded)
        {
            int status = result.Code == "user_not_found" || result.Code == "session_not_found"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest;
            return Problem(context, status, result.Code!);
        }

        SessionDetail detail = (await reads.GetSessionDetailAsync(id, actorId.Value, cancellationToken))!;
        string version = versionStamp.Issue(id, detail.ConcurrencyToken);
        return Results.Ok(SessionDetailResponse.From(detail, version));
    }

    private static async Task<IResult> CreateSessionGuestLinksAsync(
        Guid sessionId,
        CreateSessionGuestLinksRequest request,
        ICurrentUserAccessor currentUser,
        SessionInvitationUseCases useCase,
        IConfiguration configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        CreateGuestLinksResult result;
        try
        {
            result = await useCase.CreateGuestLinksAsync(
                SessionId.From(sessionId), actorId.Value, request.Count, cancellationToken);
        }
        catch (DomainException ex) when (ex.Error is DomainError.WindowClosed or DomainError.TerminalState)
        {
            return Problem(context, StatusCodes.Status422UnprocessableEntity, "session_invites_closed");
        }

        if (!result.Succeeded)
        {
            int status = result.Code == "session_not_found"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest;
            return Problem(context, status, result.Code!);
        }

        var response = result.GuestLinks!
            .Select(link => new GuestLinkInvitationResponse(
                link.GuestLinkId.Value,
                BuildGuestUrl(configuration, link.Token)))
            .ToArray();
        return Results.Ok(response);
    }

    private static async Task<IResult> RevokeSessionGuestLinkAsync(
        Guid sessionId,
        Guid guestLinkId,
        ICurrentUserAccessor currentUser,
        SessionInvitationUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        SessionCommandResult result = await useCase.RevokeGuestLinkAsync(
            SessionId.From(sessionId), actorId.Value, GuestLinkId.From(guestLinkId), cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status404NotFound, result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> RemoveSessionGuestAsync(
        Guid sessionId,
        Guid participationId,
        ICurrentUserAccessor currentUser,
        SessionInvitationUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        SessionCommandResult result;
        try
        {
            result = await useCase.RemoveSessionGuestAsync(
                SessionId.From(sessionId),
                actorId.Value,
                ParticipationId.From(participationId),
                cancellationToken);
        }
        catch (DomainException ex) when (ex.Error is DomainError.WindowClosed or DomainError.TerminalState)
        {
            return Problem(context, StatusCodes.Status422UnprocessableEntity, "session_guest_removal_closed");
        }

        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status404NotFound, result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> RespondToSessionInvitationAsync(
        Guid sessionId,
        RespondToSessionInvitationRequest request,
        ICurrentUserAccessor currentUser,
        SessionParticipationUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        ParticipationStatus? response = SessionWireFormat.ParseRsvpResponse(request.Response);
        if (response is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "session_validation");
        }

        SessionCommandResult result;
        try
        {
            result = await useCase.RespondToInvitationAsync(
                SessionId.From(sessionId), actorId.Value, response.Value, cancellationToken);
        }
        catch (DomainException ex)
        {
            return DomainProblem(context, ex, error => error switch
            {
                DomainError.WindowClosed or DomainError.TerminalState or DomainError.InvalidTransition =>
                    (StatusCodes.Status422UnprocessableEntity, "session_responses_closed"),
                DomainError.NotFound or DomainError.RemovalIsPermanent =>
                    (StatusCodes.Status404NotFound, "session_not_found"),
                _ => (StatusCodes.Status400BadRequest, "session_validation"),
            });
        }

        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status404NotFound, result.Code!);
        }

        Participation participation = result.Session!.Find(ParticipantRef.ForUser(actorId.Value))!;
        return Results.Ok(ParticipationViewResponse.From(participation));
    }

    private static async Task<IResult> LeaveSessionAsync(
        Guid sessionId,
        ICurrentUserAccessor currentUser,
        SessionParticipationUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        SessionCommandResult result;
        try
        {
            result = await useCase.LeaveSessionAsync(SessionId.From(sessionId), actorId.Value, cancellationToken);
        }
        catch (DomainException ex) when (
            ex.Error is DomainError.WindowClosed or DomainError.TerminalState or DomainError.InvalidTransition)
        {
            return Problem(context, StatusCodes.Status422UnprocessableEntity, "session_leave_closed");
        }

        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status404NotFound, result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> RequestSessionJoinAsync(
        Guid sessionId,
        ICurrentUserAccessor currentUser,
        SessionJoinRequestUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        JoinRequestCommandResult result;
        try
        {
            result = await useCase.RequestJoinAsync(SessionId.From(sessionId), actorId.Value, cancellationToken);
        }
        catch (DomainException ex)
        {
            return DomainProblem(context, ex, error => error switch
            {
                DomainError.WindowClosed or DomainError.TerminalState =>
                    (StatusCodes.Status422UnprocessableEntity, "join_request_closed"),
                DomainError.Duplicate => (StatusCodes.Status409Conflict, "join_request_exists"),
                DomainError.RemovalIsPermanent => (StatusCodes.Status404NotFound, "session_not_found"),
                _ => (StatusCodes.Status409Conflict, "join_request_exists"),
            });
        }

        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status404NotFound, result.Code!);
        }

        return Results.Created(
            $"{ApiRoutes.V1}/sessions/{sessionId}/join-requests/{result.JoinRequest!.Id.Value}",
            JoinRequestViewResponse.From(result.JoinRequest!));
    }

    private static async Task<IResult> WithdrawSessionJoinRequestAsync(
        Guid sessionId,
        ICurrentUserAccessor currentUser,
        SessionJoinRequestUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        JoinRequestCommandResult result = await useCase.WithdrawJoinRequestAsync(
            SessionId.From(sessionId), actorId.Value, cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status404NotFound, result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ListSessionJoinRequestsAsync(
        Guid sessionId,
        string? cursor,
        ICurrentUserAccessor currentUser,
        SessionJoinRequestUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (!TryDecodeCursor(cursor, SessionListScope.JoinRequests, out SessionListCursor? decodedCursor))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_cursor");
        }

        (bool found, SessionPage<JoinRequestSummary> page) = await useCase.ListSessionJoinRequestsAsync(
            SessionId.From(sessionId), actorId.Value, decodedCursor, PageSize, cancellationToken);
        if (!found)
        {
            return Problem(context, StatusCodes.Status404NotFound, "session_not_found");
        }

        page = RebindNextCursor(page, SessionListScope.JoinRequests);

        var ordinals = new MaskedParticipantAssigner();
        return Results.Ok(SessionPageResponse.From(page, s => JoinRequestViewResponse.From(s, ordinals)));
    }

    private static async Task<IResult> ApproveSessionJoinRequestAsync(
        Guid sessionId,
        Guid requestId,
        ICurrentUserAccessor currentUser,
        SessionJoinRequestUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        JoinRequestCommandResult result;
        try
        {
            result = await useCase.ApproveJoinRequestAsync(
                SessionId.From(sessionId), actorId.Value, JoinRequestId.From(requestId), cancellationToken);
        }
        catch (DomainException ex) when (ex.Error is DomainError.WindowClosed or DomainError.TerminalState)
        {
            return Problem(context, StatusCodes.Status422UnprocessableEntity, "join_request_closed");
        }

        if (!result.Succeeded)
        {
            int status = result.Code == "join_request_not_pending"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status404NotFound;
            return Problem(context, status, result.Code!);
        }

        Participation participation = result.Session!.Find(
            ParticipantRef.ForUser(result.JoinRequest!.RequesterId))!;
        return Results.Ok(ParticipationViewResponse.From(participation));
    }

    private static async Task<IResult> RejectSessionJoinRequestAsync(
        Guid sessionId,
        Guid requestId,
        ICurrentUserAccessor currentUser,
        SessionJoinRequestUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        JoinRequestCommandResult result = await useCase.RejectJoinRequestAsync(
            SessionId.From(sessionId), actorId.Value, JoinRequestId.From(requestId), cancellationToken);
        if (!result.Succeeded)
        {
            int status = result.Code == "join_request_not_pending"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status404NotFound;
            return Problem(context, status, result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> PromoteSessionWaitlistParticipantAsync(
        Guid sessionId,
        Guid participationId,
        ICurrentUserAccessor currentUser,
        SessionJoinRequestUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        SessionCommandResult result;
        try
        {
            result = await useCase.PromoteWaitlistParticipantAsync(
                SessionId.From(sessionId), actorId.Value, ParticipationId.From(participationId), cancellationToken);
        }
        catch (DomainException ex)
        {
            return DomainProblem(context, ex, error => error switch
            {
                DomainError.CapacityExceeded => (StatusCodes.Status422UnprocessableEntity, "no_session_capacity"),
                DomainError.Conflict => (StatusCodes.Status422UnprocessableEntity, "not_waitlisted"),
                DomainError.NotFound or DomainError.RemovalIsPermanent =>
                    (StatusCodes.Status404NotFound, "session_not_found"),
                _ => (StatusCodes.Status422UnprocessableEntity, "not_waitlisted"),
            });
        }

        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status404NotFound, result.Code!);
        }

        Participation participation = result.Session!.Participations
            .First(p => p.Id == ParticipationId.From(participationId));
        return Results.Ok(ParticipationViewResponse.From(participation));
    }

    private static async Task<IResult> RejectSessionWaitlistParticipantAsync(
        Guid sessionId,
        Guid participationId,
        ICurrentUserAccessor currentUser,
        SessionJoinRequestUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        SessionCommandResult result;
        try
        {
            result = await useCase.RejectWaitlistParticipantAsync(
                SessionId.From(sessionId), actorId.Value, ParticipationId.From(participationId), cancellationToken);
        }
        catch (DomainException ex) when (ex.Error is DomainError.Conflict)
        {
            return Problem(context, StatusCodes.Status422UnprocessableEntity, "not_waitlisted");
        }
        catch (DomainException ex) when (ex.Error is DomainError.NotFound or DomainError.RemovalIsPermanent)
        {
            return Problem(context, StatusCodes.Status404NotFound, "session_not_found");
        }

        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status404NotFound, result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> SetSessionReminderAsync(
        Guid sessionId,
        SetSessionReminderRequest request,
        ICurrentUserAccessor currentUser,
        SetSessionReminderUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (!SessionWireFormat.TryParseReminderChoice(request.Choice, out ReminderOffset? choice))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "session_validation");
        }

        SetSessionReminderResult result = await useCase.ExecuteAsync(
            SessionId.From(sessionId), actorId.Value, choice, cancellationToken);
        if (!result.Succeeded)
        {
            int status = result.Code == "reminder_not_eligible"
                ? StatusCodes.Status422UnprocessableEntity
                : StatusCodes.Status404NotFound;
            return Problem(context, status, result.Code!);
        }

        Participation participation = result.Session!.Find(ParticipantRef.ForUser(actorId.Value))!;
        return Results.Ok(new ReminderViewResponse(SessionWireFormat.ReminderChoice(participation.ReminderChoice)));
    }

    // ---- shared helpers -----------------------------------------------------------------------

    private static string BuildGuestUrl(IConfiguration configuration, string token)
    {
        string baseUrl = (configuration["PublicBaseUrl"] ?? "http://localhost:5173").TrimEnd('/');
        return $"{baseUrl}/g/{token}";
    }

    private static bool TryParseInstant(string? value, out Instant instant)
    {
        if (value is not null)
        {
            var parsed = InstantPattern.ExtendedIso.Parse(value);
            if (parsed.Success)
            {
                instant = parsed.Value;
                return true;
            }
        }

        instant = default;
        return false;
    }

    /// <summary>
    /// The A-072 shared decode path: a cursor is only accepted for the list that issued it — a
    /// cursor decodes but declares a different <see cref="SessionListScope"/> (or a malformed/absent
    /// scope) is rejected as invalid, never silently honored against the wrong list.
    /// </summary>
    private static bool TryDecodeCursor(string? cursor, SessionListScope expectedScope, out SessionListCursor? decoded)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            decoded = null;
            return true;
        }

        decoded = SessionListCursor.TryDecode(cursor, expectedScope);
        return decoded is not null;
    }

    /// <summary>
    /// The page's <c>NextCursor</c> is minted by the read repository without knowledge of which list
    /// issued it (A-072); rebind it here to the calling endpoint's <see cref="SessionListScope"/>
    /// before it ever reaches the client, so the token a client receives is provably bound to this
    /// list.
    /// </summary>
    private static SessionPage<T> RebindNextCursor<T>(SessionPage<T> page, SessionListScope scope)
    {
        if (page.NextCursor is null)
        {
            return page;
        }

        SessionListCursor? inner = SessionListCursor.TryDecode(page.NextCursor);
        if (inner is null)
        {
            return page;
        }

        string rebound = new SessionListCursor(inner.Value.SortKey, inner.Value.Id, scope).Encode();
        return page with { NextCursor = rebound };
    }

    /// <summary>
    /// The endpoint's own <c>400 invalid_if_match</c> pre-check (A-071): well-formed base64url of a
    /// plausible Data Protection ciphertext length, nothing more — this never calls <c>Unprotect</c>;
    /// the actual comparison against the freshly locked row happens inside
    /// <see cref="EditSessionUseCase"/>, never here against an unlocked read.
    /// </summary>
    private static bool TryDecodeVersionToken(string? ifMatch)
    {
        if (string.IsNullOrEmpty(ifMatch))
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(ifMatch);
        }
        catch (FormatException)
        {
            return false;
        }

        // A genuine token is a Data Protection ciphertext of the 20-byte sessionId‖xmin payload — the
        // AES-CBC/HMAC-SHA256 default scheme alone adds well over 60 bytes of header/IV/MAC overhead, so
        // anything shorter than this floor (e.g. a bare base64url string like "AA") is malformed shape,
        // not a value worth handing to Unprotect.
        const int MinTokenBytes = 32;
        return decoded.Length >= MinTokenBytes;
    }

    private static async Task<UserId?> ActorIdAsync(
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken) =>
        (await currentUser.GetAsync(cancellationToken))?.Id;

    private static IResult DomainProblem(
        HttpContext context,
        DomainException exception,
        Func<DomainError, (int Status, string Code)> map)
    {
        (int status, string code) = map(exception.Error);
        return Problem(context, status, code);
    }

    private static IResult Problem(HttpContext context, int status, string code) =>
        PartyUpProblemResults.Create(context, status, code);

    private static (int Status, string Code) MapEditError(DomainError error) => error switch
    {
        DomainError.ValidationFailed => (StatusCodes.Status400BadRequest, "session_validation"),
        DomainError.TerminalState => (StatusCodes.Status409Conflict, "session_terminal"),
        DomainError.WindowClosed => (StatusCodes.Status422UnprocessableEntity, "session_time_locked"),
        DomainError.CapacityExceeded => (StatusCodes.Status422UnprocessableEntity, "capacity_below_occupancy"),
        DomainError.Conflict => (StatusCodes.Status422UnprocessableEntity, "waitlist_requires_capacity"),
        _ => (StatusCodes.Status409Conflict, "session_conflict"),
    };

    private static RouteHandlerBuilder Common(RouteHandlerBuilder builder) =>
        builder
            .ProducesPartyUpProblem(StatusCodes.Status401Unauthorized)
            .ProducesPartyUpProblem(StatusCodes.Status403Forbidden)
            .ProducesPartyUpProblem(StatusCodes.Status429TooManyRequests)
            .ProducesPartyUpProblem(StatusCodes.Status500InternalServerError);

    private static RouteHandlerBuilder Unsafe(RouteHandlerBuilder builder) =>
        Common(builder)
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest)
            .ProducesPartyUpProblem(StatusCodes.Status503ServiceUnavailable);

    private static RouteHandlerBuilder BodyOperation(RouteHandlerBuilder builder) =>
        Unsafe(builder)
            .ProducesPartyUpProblem(StatusCodes.Status415UnsupportedMediaType);
}
