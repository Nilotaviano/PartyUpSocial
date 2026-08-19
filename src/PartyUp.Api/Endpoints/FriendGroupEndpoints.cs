using PartyUp.Api.Auth;
using PartyUp.Api.Contracts.FriendGroups;
using PartyUp.Api.ErrorHandling;
using PartyUp.Application.Abstractions;
using PartyUp.Application.FriendGroups;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// The FriendGroup HTTP surface (D-198/D-199), mirroring <see cref="FriendEndpoints"/>'s structure:
/// every route sits under <c>/api/v1</c>, requires <c>RequireOnboarded</c>, and inherits the group's
/// <c>ProblemAntiforgeryFilter</c> on unsafe methods. A group the actor does not
/// <see cref="FriendGroup.Involves"/> is answered identically to a nonexistent one — always
/// <c>group_not_found</c>, never a distinguishable forbidden.
/// </summary>
public static class FriendGroupEndpoints
{
    public static RouteGroupBuilder MapFriendGroupEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder groups = api.MapGroup(string.Empty)
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);

        Common(groups.MapGet("/friend-groups", ListFriendGroupsAsync)
                .WithName(ApiOperationIds.ListFriendGroups)
                .Produces<FriendGroupListResponse>());

        Common(groups.MapGet("/friend-groups/containing/{userId:guid}", ListFriendGroupsContainingUserAsync)
                .WithName(ApiOperationIds.ListFriendGroupsContainingUser)
                .Produces<FriendGroupMembershipsResponse>());

        BodyOperation(groups.MapPost("/friend-groups", CreateFriendGroupAsync)
                .WithName(ApiOperationIds.CreateFriendGroup)
                .Produces<FriendGroupResponse>(StatusCodes.Status201Created));

        Common(groups.MapGet("/friend-groups/{groupId:guid}", GetFriendGroupAsync)
                .WithName(ApiOperationIds.GetFriendGroup)
                .Produces<FriendGroupDetailResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        BodyOperation(groups.MapPatch("/friend-groups/{groupId:guid}", RenameFriendGroupAsync)
                .WithName(ApiOperationIds.RenameFriendGroup)
                .Produces<FriendGroupResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        Unsafe(groups.MapDelete("/friend-groups/{groupId:guid}", DeleteFriendGroupAsync)
                .WithName(ApiOperationIds.DeleteFriendGroup)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        BodyOperation(groups.MapPost("/friend-groups/{groupId:guid}/members", InviteFriendGroupMembersAsync)
                .WithName(ApiOperationIds.InviteFriendGroupMembers)
                .Produces<FriendGroupResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        Unsafe(groups.MapDelete(
                    "/friend-groups/{groupId:guid}/members/{userId:guid}",
                    RemoveFriendGroupMemberAsync)
                .WithName(ApiOperationIds.RemoveFriendGroupMember)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(groups.MapPost(
                    "/friend-groups/{groupId:guid}/invitation/accept",
                    AcceptFriendGroupInvitationAsync)
                .WithName(ApiOperationIds.AcceptFriendGroupInvitation)
                .Produces<FriendGroupResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(groups.MapPost(
                    "/friend-groups/{groupId:guid}/invitation/decline",
                    DeclineFriendGroupInvitationAsync)
                .WithName(ApiOperationIds.DeclineFriendGroupInvitation)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static async Task<IResult> ListFriendGroupsAsync(
        ICurrentUserAccessor currentUser,
        FriendGroupReadUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendGroupListResult result = await useCase.ListMineAsync(actorId.Value, cancellationToken);
        return Results.Ok(FriendGroupListResponse.From(result));
    }

    /// <summary>
    /// D-210: the caller's own groups that <c>userId</c> is an Accepted member of. Never 404s on the
    /// target — a user the caller shares no owned group with reads the same empty list a nonexistent
    /// one does, so the route discloses nothing about who exists.
    /// </summary>
    private static async Task<IResult> ListFriendGroupsContainingUserAsync(
        Guid userId,
        ICurrentUserAccessor currentUser,
        FriendGroupReadUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        IReadOnlyList<FriendGroupSummary> owned = await useCase.ListOwnedContainingAsync(
            actorId.Value, UserId.From(userId), cancellationToken);
        return Results.Ok(FriendGroupMembershipsResponse.From(owned));
    }

    private static async Task<IResult> CreateFriendGroupAsync(
        CreateFriendGroupRequest request,
        ICurrentUserAccessor currentUser,
        FriendGroupUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendGroupCommandResult result = await useCase.CreateAsync(actorId.Value, request.Name, cancellationToken);
        if (!result.Succeeded)
        {
            return CommandProblem(context, result.Code!);
        }

        return Results.Json(
            FriendGroupResponse.From(result.Group!),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetFriendGroupAsync(
        Guid groupId,
        ICurrentUserAccessor currentUser,
        FriendGroupReadUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendGroupDetail? detail = await useCase.GetAsync(
            actorId.Value, FriendGroupId.From(groupId), cancellationToken);
        return detail is null
            ? Problem(context, StatusCodes.Status404NotFound, "group_not_found")
            : Results.Ok(FriendGroupDetailResponse.From(detail));
    }

    private static async Task<IResult> RenameFriendGroupAsync(
        Guid groupId,
        RenameFriendGroupRequest request,
        ICurrentUserAccessor currentUser,
        FriendGroupUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendGroupCommandResult result = await useCase.RenameAsync(
            actorId.Value, FriendGroupId.From(groupId), request.Name, cancellationToken);
        return result.Succeeded
            ? Results.Ok(FriendGroupResponse.From(result.Group!))
            : CommandProblem(context, result.Code!);
    }

    private static async Task<IResult> DeleteFriendGroupAsync(
        Guid groupId,
        ICurrentUserAccessor currentUser,
        FriendGroupUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendGroupCommandResult result = await useCase.DeleteAsync(
            actorId.Value, FriendGroupId.From(groupId), cancellationToken);
        return result.Succeeded
            ? Results.NoContent()
            : CommandProblem(context, result.Code!);
    }

    private static async Task<IResult> InviteFriendGroupMembersAsync(
        Guid groupId,
        InviteFriendGroupMembersRequest request,
        ICurrentUserAccessor currentUser,
        FriendGroupUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        List<UserId> inviteeIds = (request.UserIds ?? []).Select(UserId.From).ToList();
        FriendGroupCommandResult result = await useCase.InviteAsync(
            actorId.Value, FriendGroupId.From(groupId), inviteeIds, cancellationToken);
        return result.Succeeded
            ? Results.Ok(FriendGroupResponse.From(result.Group!))
            : CommandProblem(context, result.Code!);
    }

    /// <summary>
    /// Owner path removes the target row (member or pending invite alike — the aggregate's
    /// <c>RemoveMember</c> guard accepts either status, D-199); self path is <c>Leave</c>. The two
    /// share one route because the wire distinction (remove vs. revoke) is not a distinct actor-facing
    /// resource, only who the target is relative to the caller.
    /// </summary>
    private static async Task<IResult> RemoveFriendGroupMemberAsync(
        Guid groupId,
        Guid userId,
        ICurrentUserAccessor currentUser,
        FriendGroupUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        var target = UserId.From(userId);
        FriendGroupCommandResult result = actorId.Value == target
            ? await useCase.LeaveAsync(actorId.Value, FriendGroupId.From(groupId), cancellationToken)
            : await useCase.RemoveMemberAsync(actorId.Value, FriendGroupId.From(groupId), target, cancellationToken);
        return result.Succeeded
            ? Results.NoContent()
            : CommandProblem(context, result.Code!);
    }

    private static async Task<IResult> AcceptFriendGroupInvitationAsync(
        Guid groupId,
        ICurrentUserAccessor currentUser,
        FriendGroupUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendGroupCommandResult result = await useCase.AcceptInviteAsync(
            actorId.Value, FriendGroupId.From(groupId), cancellationToken);
        return result.Succeeded
            ? Results.Ok(FriendGroupResponse.From(result.Group!))
            : CommandProblem(context, result.Code!);
    }

    private static async Task<IResult> DeclineFriendGroupInvitationAsync(
        Guid groupId,
        ICurrentUserAccessor currentUser,
        FriendGroupUseCases useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendGroupCommandResult result = await useCase.DeclineInviteAsync(
            actorId.Value, FriendGroupId.From(groupId), cancellationToken);
        return result.Succeeded
            ? Results.NoContent()
            : CommandProblem(context, result.Code!);
    }

    private static async Task<UserId?> ActorIdAsync(
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken) =>
        (await currentUser.GetAsync(cancellationToken))?.Id;

    private static IResult CommandProblem(HttpContext context, string code) =>
        Problem(context, StatusFor(code), code);

    private static int StatusFor(string code) => code switch
    {
        "validation_failed" => StatusCodes.Status400BadRequest,
        "group_not_found" or "user_not_found" => StatusCodes.Status404NotFound,
        "group_limit_reached" or "group_member_limit_reached" or "group_name_taken" =>
            StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };

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
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest);

    private static RouteHandlerBuilder BodyOperation(RouteHandlerBuilder builder) =>
        Unsafe(builder)
            .ProducesPartyUpProblem(StatusCodes.Status415UnsupportedMediaType);
}
