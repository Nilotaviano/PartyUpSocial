using NodaTime;
using PartyUp.Api.Auth;
using PartyUp.Api.Contracts.Polls;
using PartyUp.Api.Contracts.Sessions;
using PartyUp.Api.ErrorHandling;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// The registered-user poll HTTP surface (PHASE6_DESIGN.md §4), mirroring
/// <see cref="SessionEndpoints"/>'s structure: every route sits under <c>/api/v1</c>, requires
/// <c>RequireOnboarded</c>, and inherits the group's <c>ProblemAntiforgeryFilter</c> on unsafe
/// methods. Rate limiting is by the four new poll dimensions (<c>poll-write</c>/<c>poll-attach</c>/
/// <c>poll-vote</c>/<c>poll-list-read</c>), applied by the global chained limiter. Every read and
/// write resolves through the shipped <see cref="Domain.Visibility.VisibilityEngine"/> and fails
/// closed: a hidden poll is indistinguishable from a missing one, always <c>poll_not_found</c>.
/// </summary>
public static class PollEndpoints
{
    public static RouteGroupBuilder MapPollEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder polls = api.MapGroup(string.Empty)
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);

        BodyOperation(polls.MapPost("/polls", CreatePollAsync)
                .WithName(ApiOperationIds.CreatePoll)
                .Produces<CreatePollResponse>(StatusCodes.Status201Created))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Common(polls.MapGet("/polls/{pollId:guid}", GetPollAsync)
                .WithName(ApiOperationIds.GetPoll)
                .Produces<PollDetailResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        BodyOperation(polls.MapPost("/polls/{pollId:guid}/options", SuggestPollOptionAsync)
                .WithName(ApiOperationIds.SuggestPollOption)
                .Produces<PollDetailResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(polls.MapDelete("/polls/{pollId:guid}/options/{optionId:guid}", DeletePollOptionAsync)
                .WithName(ApiOperationIds.DeletePollOption)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        BodyOperation(polls.MapPut("/polls/{pollId:guid}/options/{optionId:guid}/vote", VoteOnPollOptionAsync)
                .WithName(ApiOperationIds.VoteOnPollOption)
                .Produces<PollDetailResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        BodyOperation(polls.MapPost("/polls/{pollId:guid}/finalize", FinalizePollAsync)
                .WithName(ApiOperationIds.FinalizePoll)
                .Produces<FinalizePollResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(polls.MapPost("/polls/{pollId:guid}/cancel", CancelPollAsync)
                .WithName(ApiOperationIds.CancelPoll)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        BodyOperation(polls.MapPost("/polls/{pollId:guid}/guest-links", CreatePollGuestLinksAsync)
                .WithName(ApiOperationIds.CreatePollGuestLinks)
                .Produces<IReadOnlyList<GuestLinkInvitationResponse>>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(polls.MapDelete("/polls/{pollId:guid}/guest-links/{guestLinkId:guid}", RevokePollGuestLinkAsync)
                .WithName(ApiOperationIds.RevokePollGuestLink)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        Unsafe(polls.MapPost("/polls/{pollId:guid}/guests/{guestIdentityId:guid}/remove", RemovePollGuestAsync)
                .WithName(ApiOperationIds.RemovePollGuest)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(polls.MapPost("/polls/{pollId:guid}/join-requests", RequestPollJoinAsync)
                .WithName(ApiOperationIds.RequestPollJoin)
                .Produces<PollJoinRequestViewResponse>(StatusCodes.Status201Created))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(polls.MapDelete("/polls/{pollId:guid}/join-requests/mine", WithdrawPollJoinRequestAsync)
                .WithName(ApiOperationIds.WithdrawPollJoinRequest)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Common(polls.MapGet("/polls/{pollId:guid}/join-requests", ListPollJoinRequestsAsync)
                .WithName(ApiOperationIds.ListPollJoinRequests)
                .Produces<IReadOnlyList<PollJoinRequestViewResponse>>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(polls.MapPost(
                    "/polls/{pollId:guid}/join-requests/{requestId:guid}/approve",
                    ApprovePollJoinRequestAsync)
                .WithName(ApiOperationIds.ApprovePollJoinRequest)
                .Produces<PollJoinRequestViewResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict)
            .ProducesPartyUpProblem(StatusCodes.Status422UnprocessableEntity);

        Unsafe(polls.MapPost(
                    "/polls/{pollId:guid}/join-requests/{requestId:guid}/reject",
                    RejectPollJoinRequestAsync)
                .WithName(ApiOperationIds.RejectPollJoinRequest)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        return api;
    }

    // ---- handlers ---------------------------------------------------------------------------

    private static async Task<IResult> CreatePollAsync(
        CreatePollRequest request,
        ICurrentUserAccessor currentUser,
        CreatePollUseCase useCase,
        PollReadUseCases reads,
        IConfiguration configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        Domain.Kernel.Platform? platform;
        SessionGame? game;
        PollDeadlineChoice? deadlineChoice;
        try
        {
            platform = SessionWireFormat.ParsePlatform(request.Platform);
            game = request.Game is { } gameRequest ? gameRequest.ToDomain() : null;
            deadlineChoice = PollWireFormat.ParseDeadlineChoice(request.DeadlineChoice);
            if (request.Game is not null && game is null)
            {
                return Problem(context, StatusCodes.Status400BadRequest, "poll_validation");
            }
        }
        catch (FormatException)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "poll_validation");
        }

        List<Instant> optionStartTimes = [];
        foreach (string raw in request.OptionStartTimes ?? [])
        {
            if (!PollWireFormat.TryParseInstant(raw, out Instant parsed))
            {
                return Problem(context, StatusCodes.Status400BadRequest, "poll_validation");
            }

            optionStartTimes.Add(parsed);
        }

        CreatePollCommand command = new(
            actorId.Value,
            request.Title ?? string.Empty,
            request.DurationMinutes,
            platform,
            game,
            request.Notes,
            request.MinAttendance,
            request.MaxCapacity,
            request.IsPrivate,
            optionStartTimes,
            deadlineChoice,
            (request.InviteeUserIds ?? []).Select(UserId.From).ToArray(),
            request.GuestLinkCount);

        CreatePollResult result;
        try
        {
            result = await useCase.ExecuteAsync(command, cancellationToken);
        }
        catch (DomainException)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "poll_validation");
        }

        if (!result.Succeeded)
        {
            int status = result.Code switch
            {
                "user_not_found" => StatusCodes.Status404NotFound,
                "poll_option_cap" => StatusCodes.Status422UnprocessableEntity,
                _ => StatusCodes.Status400BadRequest,
            };
            return Problem(context, status, result.Code!);
        }

        PollDetail detail = (await reads.GetPollDetailAsync(result.Poll!.Id, actorId.Value, cancellationToken))!;
        var guestLinks = result.GuestLinks!
            .Select(link => new GuestLinkInvitationResponse(link.GuestLinkId.Value, BuildGuestUrl(configuration, link.Token)))
            .ToArray();

        return Results.Created(
            $"{ApiRoutes.V1}/polls/{result.Poll.Id.Value}",
            new CreatePollResponse(PollDetailResponse.From(detail), guestLinks));
    }

    private static async Task<IResult> GetPollAsync(
        Guid pollId,
        ICurrentUserAccessor currentUser,
        PollReadUseCases reads,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        PollDetail? detail = await reads.GetPollDetailAsync(PollId.From(pollId), actorId.Value, cancellationToken);
        if (detail is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        return Results.Ok(PollDetailResponse.From(detail));
    }

    private static async Task<IResult> SuggestPollOptionAsync(
        Guid pollId,
        SuggestPollOptionRequest request,
        ICurrentUserAccessor currentUser,
        PollOptionUseCases useCase,
        PollReadUseCases reads,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (!PollWireFormat.TryParseInstant(request.StartTime, out Instant startTime))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "poll_validation");
        }

        SuggestOptionResult result = await useCase.SuggestOptionAsync(
            PollId.From(pollId), actorId.Value, startTime, cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusToCodeStatus(result.Code!), result.Code!);
        }

        PollDetail detail = (await reads.GetPollDetailAsync(PollId.From(pollId), actorId.Value, cancellationToken))!;
        return Results.Ok(PollDetailResponse.From(detail));
    }

    private static async Task<IResult> DeletePollOptionAsync(
        Guid pollId,
        Guid optionId,
        ICurrentUserAccessor currentUser,
        PollOptionUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        PollCommandResult result = await useCase.DeleteOptionAsync(
            PollId.From(pollId), actorId.Value, PollOptionId.From(optionId), cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusToCodeStatus(result.Code!), result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> VoteOnPollOptionAsync(
        Guid pollId,
        Guid optionId,
        VoteOnPollOptionRequest request,
        ICurrentUserAccessor currentUser,
        PollVoteUseCases useCase,
        PollReadUseCases reads,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        VoteChoice? choice = PollWireFormat.ParseVoteChoice(request.Choice);
        if (choice is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "poll_validation");
        }

        PollCommandResult result = await useCase.VoteAsync(
            PollId.From(pollId), actorId.Value, PollOptionId.From(optionId), choice.Value, cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusToCodeStatus(result.Code!), result.Code!);
        }

        PollDetail detail = (await reads.GetPollDetailAsync(PollId.From(pollId), actorId.Value, cancellationToken))!;
        return Results.Ok(PollDetailResponse.From(detail));
    }

    private static async Task<IResult> FinalizePollAsync(
        Guid pollId,
        FinalizePollRequest request,
        ICurrentUserAccessor currentUser,
        FinalizePollUseCase useCase,
        PollReadUseCases reads,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FinalizePollResult result = await useCase.FinalizeAsync(
            PollId.From(pollId), actorId.Value, PollOptionId.From(request.OptionId), cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusToCodeStatus(result.Code!), result.Code!);
        }

        PollDetail detail = (await reads.GetPollDetailAsync(PollId.From(pollId), actorId.Value, cancellationToken))!;
        return Results.Ok(new FinalizePollResponse(result.Session!.Id.Value, PollDetailResponse.From(detail)));
    }

    private static async Task<IResult> CancelPollAsync(
        Guid pollId,
        ICurrentUserAccessor currentUser,
        CancelPollUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        PollCommandResult result = await useCase.CancelAsync(PollId.From(pollId), actorId.Value, cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusToCodeStatus(result.Code!), result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> CreatePollGuestLinksAsync(
        Guid pollId,
        CreatePollGuestLinksRequest request,
        ICurrentUserAccessor currentUser,
        PollGuestManagementUseCases useCase,
        IConfiguration configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        CreatePollGuestLinksResult result = await useCase.CreateGuestLinksAsync(
            PollId.From(pollId), actorId.Value, request.Count, cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusToCodeStatus(result.Code!), result.Code!);
        }

        var response = result.GuestLinks!
            .Select(link => new GuestLinkInvitationResponse(link.GuestLinkId.Value, BuildGuestUrl(configuration, link.Token)))
            .ToArray();
        return Results.Ok(response);
    }

    private static async Task<IResult> RevokePollGuestLinkAsync(
        Guid pollId,
        Guid guestLinkId,
        ICurrentUserAccessor currentUser,
        PollGuestManagementUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        PollCommandResult result = await useCase.RevokeGuestLinkAsync(
            PollId.From(pollId), actorId.Value, GuestLinkId.From(guestLinkId), cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusToCodeStatus(result.Code!), result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> RemovePollGuestAsync(
        Guid pollId,
        Guid guestIdentityId,
        ICurrentUserAccessor currentUser,
        PollGuestManagementUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        PollCommandResult result = await useCase.RemoveGuestAsync(
            PollId.From(pollId), actorId.Value, GuestIdentityId.From(guestIdentityId), cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusToCodeStatus(result.Code!), result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> RequestPollJoinAsync(
        Guid pollId,
        ICurrentUserAccessor currentUser,
        PollJoinRequestUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        PollJoinRequestCommandResult result = await useCase.RequestJoinAsync(
            PollId.From(pollId), actorId.Value, cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusToCodeStatus(result.Code!), result.Code!);
        }

        return Results.Created(
            $"{ApiRoutes.V1}/polls/{pollId}/join-requests/{result.JoinRequest!.Id.Value}",
            PollJoinRequestViewResponse.From(result.JoinRequest!));
    }

    private static async Task<IResult> WithdrawPollJoinRequestAsync(
        Guid pollId,
        ICurrentUserAccessor currentUser,
        PollJoinRequestUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        PollJoinRequestCommandResult result = await useCase.WithdrawJoinRequestAsync(
            PollId.From(pollId), actorId.Value, cancellationToken);
        if (!result.Succeeded)
        {
            return Problem(context, StatusToCodeStatus(result.Code!), result.Code!);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ListPollJoinRequestsAsync(
        Guid pollId,
        ICurrentUserAccessor currentUser,
        IPollRepository polls,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        Poll? poll = await polls.FindNoTrackingAsync(PollId.From(pollId), cancellationToken);
        if (poll is null || poll.OrganizerId != actorId.Value)
        {
            return Problem(context, StatusCodes.Status404NotFound, "poll_not_found");
        }

        var response = poll.JoinRequests.Select(PollJoinRequestViewResponse.From).ToArray();
        return Results.Ok(response);
    }

    private static async Task<IResult> ApprovePollJoinRequestAsync(
        Guid pollId,
        Guid requestId,
        ICurrentUserAccessor currentUser,
        PollJoinRequestUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        PollJoinRequestCommandResult result = await useCase.ApproveJoinRequestAsync(
            PollId.From(pollId), actorId.Value, JoinRequestId.From(requestId), cancellationToken);
        if (!result.Succeeded)
        {
            int status = result.Code == "join_request_not_pending"
                ? StatusCodes.Status409Conflict
                : StatusToCodeStatus(result.Code!);
            return Problem(context, status, result.Code!);
        }

        return Results.Ok(PollJoinRequestViewResponse.From(result.JoinRequest!));
    }

    private static async Task<IResult> RejectPollJoinRequestAsync(
        Guid pollId,
        Guid requestId,
        ICurrentUserAccessor currentUser,
        PollJoinRequestUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        PollJoinRequestCommandResult result = await useCase.RejectJoinRequestAsync(
            PollId.From(pollId), actorId.Value, JoinRequestId.From(requestId), cancellationToken);
        if (!result.Succeeded)
        {
            int status = result.Code is "join_request_not_pending" or "poll_invalid_transition"
                ? StatusCodes.Status409Conflict
                : StatusToCodeStatus(result.Code!);
            return Problem(context, status, result.Code!);
        }

        return Results.NoContent();
    }

    // ---- shared helpers -----------------------------------------------------------------------

    /// <summary>
    /// The single status map for every <see cref="PollOptionUseCases.CodeFor"/>-produced code
    /// (shared by every poll command handler above): <c>poll_not_found</c>/<c>poll_option_not_found</c>/
    /// <c>poll_guest_not_found</c>/<c>poll_participant_removed</c> are all fail-closed 404s (D-114/D-125's
    /// hidden-equals-missing rule extended to a removed voter, §12-24); <c>poll_option_cap</c>/
    /// <c>poll_voting_closed</c> are 422 (D-171/D-079 window guards); <c>poll_terminal</c>/
    /// <c>poll_invalid_transition</c>/<c>poll_conflict</c> are 409; everything else is 400.
    /// </summary>
    private static int StatusToCodeStatus(string code) => code switch
    {
        "poll_not_found" or "poll_option_not_found" or "poll_guest_not_found" or "poll_participant_removed" =>
            StatusCodes.Status404NotFound,
        "poll_option_cap" or "poll_voting_closed" => StatusCodes.Status422UnprocessableEntity,
        "poll_terminal" or "poll_invalid_transition" or "poll_conflict" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

    private static string BuildGuestUrl(IConfiguration configuration, string token)
    {
        string baseUrl = (configuration["PublicBaseUrl"] ?? "http://localhost:5173").TrimEnd('/');
        return $"{baseUrl}/g/{token}";
    }

    private static async Task<UserId?> ActorIdAsync(
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken) =>
        (await currentUser.GetAsync(cancellationToken))?.Id;

    private static IResult Problem(HttpContext context, int status, string code) =>
        PartyUpProblemResults.Create(context, status, code);

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
