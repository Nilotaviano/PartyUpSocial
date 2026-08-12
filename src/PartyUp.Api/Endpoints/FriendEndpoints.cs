using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Text;
using PartyUp.Api.Auth;
using PartyUp.Api.ErrorHandling;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Endpoints;

public static class FriendEndpoints
{
    public static RouteGroupBuilder MapFriendEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder friends = api.MapGroup(string.Empty)
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);

        Common(friends.MapGet("/friend-share", GetFriendShareAsync)
                .WithName(ApiOperationIds.GetFriendShare)
                .Produces<FriendShareResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status500InternalServerError);

        Common(friends.MapGet("/users/by-username/{username}", GetUserProfileAsync)
                .WithName(ApiOperationIds.GetUserProfile)
                .Produces<UsernameProfileResolutionResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Common(friends.MapGet("/friend-codes/{friendCode}", ResolveFriendCodeAsync)
                .WithName(ApiOperationIds.ResolveFriendCode)
                .Produces<FriendCodeResolutionResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest)
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Common(friends.MapGet("/friends", ListFriendsAsync)
                .WithName(ApiOperationIds.ListFriends)
                .Produces<FriendListResponse>());

        Common(friends.MapGet("/friend-requests", ListFriendRequestsAsync)
                .WithName(ApiOperationIds.ListFriendRequests)
                .Produces<FriendRequestListResponse>());

        BodyOperation(friends.MapPost("/friend-requests", SendFriendRequestAsync)
                .WithName(ApiOperationIds.SendFriendRequest)
                .Produces<FriendshipResourceResponse>(StatusCodes.Status200OK)
                .Produces<FriendshipResourceResponse>(StatusCodes.Status201Created))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        Unsafe(friends.MapPost(
                    "/friend-requests/{friendshipId}/accept",
                    AcceptFriendRequestAsync)
                .WithName(ApiOperationIds.AcceptFriendRequest)
                .Produces<FriendshipResourceResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        Unsafe(friends.MapPost(
                    "/friend-requests/{friendshipId}/reject",
                    RejectFriendRequestAsync)
                .WithName(ApiOperationIds.RejectFriendRequest)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        Unsafe(friends.MapDelete(
                    "/friend-requests/{friendshipId}",
                    CancelFriendRequestAsync)
                .WithName(ApiOperationIds.CancelFriendRequest)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound)
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        Unsafe(friends.MapDelete("/friends/{friendshipId}", UnfriendAsync)
                .WithName(ApiOperationIds.Unfriend)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Common(friends.MapGet("/blocks", ListBlocksAsync)
                .WithName(ApiOperationIds.ListBlocks)
                .Produces<BlockedUserListResponse>());

        BodyOperation(friends.MapPost("/blocks", BlockUserAsync)
                .WithName(ApiOperationIds.BlockUser)
                .Produces<BlockedUserResponse>(StatusCodes.Status201Created))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(friends.MapDelete("/blocks/{blockId}", UnblockUserAsync)
                .WithName(ApiOperationIds.UnblockUser)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(friends.MapPost("/friends/imports/steam", ImportSteamFriendsAsync)
                .WithName(ApiOperationIds.ImportSteamFriends)
                .Produces<SteamFriendImportResponse>())
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static async Task<IResult> GetFriendShareAsync(
        ICurrentUserAccessor currentUser,
        GetFriendShareUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendShare? share = await useCase.ExecuteAsync(actorId.Value, cancellationToken);
        return share is null
            ? Problem(context, StatusCodes.Status500InternalServerError, "internal_error")
            : Results.Ok(new FriendShareResponse(
                share.FriendCode,
                share.AddUrl,
                share.ProfileUrl));
    }

    private static async Task<IResult> GetUserProfileAsync(
        string username,
        ICurrentUserAccessor currentUser,
        GetProfileUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        UsernameProfileResolution? resolution = await useCase.ExecuteAsync(
            actorId.Value,
            username,
            cancellationToken);
        return resolution is null
            ? Problem(context, StatusCodes.Status404NotFound, "user_not_found")
            : Results.Ok(new UsernameProfileResolutionResponse(
                ProfileViewResponse.From(resolution.Profile)));
    }

    private static async Task<IResult> ResolveFriendCodeAsync(
        string friendCode,
        ICurrentUserAccessor currentUser,
        ResolveFriendCodeUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        ResolveFriendCodeResult result = await useCase.ExecuteAsync(
            actorId.Value,
            friendCode,
            cancellationToken);
        return result switch
        {
            FriendCodeInvalid => Problem(
                context,
                StatusCodes.Status400BadRequest,
                "friend_code_invalid"),
            FriendCodeHidden => Problem(
                context,
                StatusCodes.Status404NotFound,
                "user_not_found"),
            FriendCodeFound found => Results.Ok(new FriendCodeResolutionResponse(
                ProfileViewResponse.From(found.Resolution.Profile))),
            _ => Problem(context, StatusCodes.Status500InternalServerError, "internal_error"),
        };
    }

    private static async Task<IResult> ListFriendsAsync(
        ICurrentUserAccessor currentUser,
        ListFriendsUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendList result = await useCase.ExecuteAsync(actorId.Value, cancellationToken);
        return Results.Ok(new FriendListResponse(
            result.Items.Select(FriendItemResponse.From).ToArray()));
    }

    private static async Task<IResult> ListFriendRequestsAsync(
        ICurrentUserAccessor currentUser,
        ListFriendRequestsUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendRequestList result = await useCase.ExecuteAsync(actorId.Value, cancellationToken);
        return Results.Ok(new FriendRequestListResponse(
            result.Incoming.Select(FriendRequestItemResponse.From).ToArray(),
            result.Outgoing.Select(FriendRequestItemResponse.From).ToArray()));
    }

    private static async Task<IResult> SendFriendRequestAsync(
        TargetUsernameRequest request,
        ICurrentUserAccessor currentUser,
        SendFriendRequestUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendGraphCommandResult result = await useCase.ExecuteAsync(
            actorId.Value,
            request.TargetUsername,
            cancellationToken);
        if (!result.Succeeded)
        {
            return CommandProblem(context, result.Code!);
        }

        FriendshipResourceResponse response = FriendshipResourceResponse.From(
            result.Friendship!);
        return Results.Json(
            response,
            statusCode: result.Created
                ? StatusCodes.Status201Created
                : StatusCodes.Status200OK);
    }

    private static Task<IResult> AcceptFriendRequestAsync(
        string friendshipId,
        ICurrentUserAccessor currentUser,
        AcceptFriendRequestUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken) =>
        ExecuteFriendshipActionAsync(
            friendshipId,
            "friend_request_not_found",
            currentUser,
            (actorId, id, ct) => useCase.ExecuteAsync(actorId, id, ct),
            requireResource: true,
            context,
            cancellationToken);

    private static Task<IResult> RejectFriendRequestAsync(
        string friendshipId,
        ICurrentUserAccessor currentUser,
        RejectFriendRequestUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken) =>
        ExecuteFriendshipActionAsync(
            friendshipId,
            "friend_request_not_found",
            currentUser,
            (actorId, id, ct) => useCase.ExecuteAsync(actorId, id, ct),
            requireResource: false,
            context,
            cancellationToken);

    private static Task<IResult> CancelFriendRequestAsync(
        string friendshipId,
        ICurrentUserAccessor currentUser,
        CancelFriendRequestUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken) =>
        ExecuteFriendshipActionAsync(
            friendshipId,
            "friend_request_not_found",
            currentUser,
            (actorId, id, ct) => useCase.ExecuteAsync(actorId, id, ct),
            requireResource: false,
            context,
            cancellationToken);

    private static Task<IResult> UnfriendAsync(
        string friendshipId,
        ICurrentUserAccessor currentUser,
        UnfriendUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken) =>
        ExecuteFriendshipActionAsync(
            friendshipId,
            "friendship_not_found",
            currentUser,
            (actorId, id, ct) => useCase.ExecuteAsync(actorId, id, ct),
            requireResource: false,
            context,
            cancellationToken);

    private static async Task<IResult> ExecuteFriendshipActionAsync(
        string friendshipId,
        string malformedCode,
        ICurrentUserAccessor currentUser,
        Func<UserId, FriendshipId, CancellationToken, Task<FriendGraphCommandResult>> execute,
        bool requireResource,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(friendshipId, out Guid value))
        {
            return Problem(context, StatusCodes.Status404NotFound, malformedCode);
        }

        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        FriendGraphCommandResult result = await execute(
            actorId.Value,
            FriendshipId.From(value),
            cancellationToken);
        if (!result.Succeeded)
        {
            return CommandProblem(context, result.Code!);
        }

        return requireResource
            ? Results.Ok(FriendshipResourceResponse.From(result.Friendship!))
            : Results.NoContent();
    }

    private static async Task<IResult> ListBlocksAsync(
        ICurrentUserAccessor currentUser,
        ListBlockedUsersUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        BlockedUserList result = await useCase.ExecuteAsync(actorId.Value, cancellationToken);
        return Results.Ok(new BlockedUserListResponse(
            result.Items.Select(BlockedUserResponse.From).ToArray()));
    }

    private static async Task<IResult> BlockUserAsync(
        TargetUsernameRequest request,
        ICurrentUserAccessor currentUser,
        BlockUserUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        BlockGraphCommandResult result = await useCase.ExecuteAsync(
            actorId.Value,
            request.TargetUsername,
            cancellationToken);
        return result.Succeeded
            ? Results.Json(
                BlockedUserResponse.From(result.BlockedUser!),
                statusCode: StatusCodes.Status201Created)
            : CommandProblem(context, result.Code!);
    }

    private static async Task<IResult> UnblockUserAsync(
        string blockId,
        ICurrentUserAccessor currentUser,
        UnblockUserUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(blockId, out Guid value))
        {
            return Problem(context, StatusCodes.Status404NotFound, "block_not_found");
        }

        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        BlockGraphCommandResult result = await useCase.ExecuteAsync(
            actorId.Value,
            BlockId.From(value),
            cancellationToken);
        return result.Succeeded
            ? Results.NoContent()
            : CommandProblem(context, result.Code!);
    }

    private static async Task<IResult> ImportSteamFriendsAsync(
        ICurrentUserAccessor currentUser,
        ImportSteamFriendsUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        ImportSteamFriendsResult result = await useCase.ExecuteAsync(
            actorId.Value,
            cancellationToken);
        return result is SteamImportNotLinked
            ? Problem(context, StatusCodes.Status409Conflict, "steam_not_linked")
            : Results.Ok(SteamFriendImportResponse.From(result));
    }

    private static async Task<UserId?> ActorIdAsync(
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken) =>
        (await currentUser.GetAsync(cancellationToken))?.Id;

    private static IResult CommandProblem(HttpContext context, string code) =>
        Problem(context, StatusFor(code), code);

    private static int StatusFor(string code) => code switch
    {
        "validation_failed" or "friend_request_self" or "block_self" =>
            StatusCodes.Status400BadRequest,
        "user_not_found" or "friend_request_not_found" or
            "friendship_not_found" or "block_not_found" =>
            StatusCodes.Status404NotFound,
        "friend_request_already_exists" or "friendship_already_exists" or
            "friend_request_not_pending" =>
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

public sealed record TargetUsernameRequest(string? TargetUsername);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendShareResponse(
    string FriendCode,
    string AddUrl,
    string ProfileUrl);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UsernameProfileResolutionResponse(ProfileViewResponse Profile);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendCodeResolutionResponse(ProfileViewResponse Profile);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "disclosure")]
[JsonDerivedType(typeof(UsernameOnlyProfileResponse), "username_only")]
[JsonDerivedType(typeof(FullProfileResponse), "full")]
public abstract record ProfileViewResponse
{
    protected ProfileViewResponse(FriendRelationshipResponse relationship) =>
        Relationship = relationship;

    public FriendRelationshipResponse Relationship { get; }

    public static ProfileViewResponse From(ProfileView profile) => profile switch
    {
        UsernameOnlyProfile usernameOnly => new UsernameOnlyProfileResponse(
            usernameOnly.Username,
            FriendRelationshipResponse.From(usernameOnly.Relationship)),
        FullProfile full => new FullProfileResponse(
            full.UserId.Value,
            full.Username,
            full.DisplayName,
            AvatarResponse.From(full.Avatar),
            full.Platforms.Select(PublicPlatformResponse.From).ToArray(),
            FriendRelationshipResponse.From(full.Relationship)),
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UsernameOnlyProfileResponse : ProfileViewResponse
{
    public UsernameOnlyProfileResponse(
        string username,
        FriendRelationshipResponse relationship)
        : base(relationship) => Username = username;

    public string Username { get; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FullProfileResponse : ProfileViewResponse
{
    public FullProfileResponse(
        Guid userId,
        string username,
        string displayName,
        AvatarResponse avatar,
        IReadOnlyList<PublicPlatformResponse> platforms,
        FriendRelationshipResponse relationship)
        : base(relationship)
    {
        UserId = userId;
        Username = username;
        DisplayName = displayName;
        Avatar = avatar;
        Platforms = platforms;
    }

    public Guid UserId { get; }

    public string Username { get; }

    public string DisplayName { get; }

    public AvatarResponse Avatar { get; }

    public IReadOnlyList<PublicPlatformResponse> Platforms { get; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendRelationshipResponse(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? FriendshipId)
{
    public static FriendRelationshipResponse From(FriendRelationship relationship) =>
        new(
            relationship.Kind switch
            {
                FriendRelationshipKind.None => "none",
                FriendRelationshipKind.IncomingPending => "incoming_pending",
                FriendRelationshipKind.OutgoingPending => "outgoing_pending",
                FriendRelationshipKind.Accepted => "accepted",
                _ => throw new ArgumentOutOfRangeException(nameof(relationship)),
            },
            relationship.FriendshipId?.Value);
}

public sealed record PublicPlatformResponse(string Platform, string Handle)
{
    public static PublicPlatformResponse From(PublicPlatform platform) =>
        new(platform.Platform.ToString(), platform.Handle);
}

public sealed record FriendListResponse(IReadOnlyList<FriendItemResponse> Items);

public sealed record FriendItemResponse(
    Guid FriendshipId,
    Guid UserId,
    string Username,
    string DisplayName,
    AvatarResponse Avatar,
    IReadOnlyList<PublicPlatformResponse> Platforms)
{
    public static FriendItemResponse From(FriendItem item) =>
        new(
            item.FriendshipId.Value,
            item.UserId.Value,
            item.Username,
            item.DisplayName,
            AvatarResponse.From(item.Avatar),
            item.Platforms.Select(PublicPlatformResponse.From).ToArray());
}

public sealed record FriendRequestListResponse(
    IReadOnlyList<FriendRequestItemResponse> Incoming,
    IReadOnlyList<FriendRequestItemResponse> Outgoing);

public sealed record FriendRequestItemResponse(
    Guid FriendshipId,
    string Username,
    string RequestedAt)
{
    public static FriendRequestItemResponse From(FriendRequestItem item) =>
        new(item.FriendshipId.Value, item.Username, Format(item.RequestedAt));

    private static string Format(Instant value) => InstantPattern.ExtendedIso.Format(value);
}

public sealed record FriendshipResourceResponse(
    Guid FriendshipId,
    string Status,
    string Direction,
    string CounterpartUsername,
    string RequestedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AcceptedAt)
{
    public static FriendshipResourceResponse From(FriendshipResource resource) =>
        new(
            resource.FriendshipId.Value,
            resource.Status,
            resource.Direction,
            resource.CounterpartUsername,
            InstantPattern.ExtendedIso.Format(resource.RequestedAt),
            resource.AcceptedAt is { } acceptedAt
                ? InstantPattern.ExtendedIso.Format(acceptedAt)
                : null);
}

public sealed record BlockedUserListResponse(IReadOnlyList<BlockedUserResponse> Items);

public sealed record BlockedUserResponse(Guid BlockId, string Username, string CreatedAt)
{
    public static BlockedUserResponse From(BlockedUserItem item) =>
        new(
            item.BlockId.Value,
            item.Username,
            InstantPattern.ExtendedIso.Format(item.CreatedAt));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "state")]
[JsonDerivedType(typeof(ReadySteamFriendImportResponse), "ready")]
[JsonDerivedType(typeof(PrivateProfileSteamFriendImportResponse), "private_profile")]
[JsonDerivedType(typeof(ZeroFriendsSteamFriendImportResponse), "zero_friends")]
[JsonDerivedType(typeof(ZeroMatchesSteamFriendImportResponse), "zero_matches")]
[JsonDerivedType(typeof(NotConfiguredSteamFriendImportResponse), "not_configured")]
[JsonDerivedType(typeof(UpstreamFailureSteamFriendImportResponse), "upstream_failure")]
public abstract record SteamFriendImportResponse
{
    public static SteamFriendImportResponse From(ImportSteamFriendsResult result) => result switch
    {
        SteamImportReady ready => new ReadySteamFriendImportResponse(
            ready.Matches.Select(SteamMatchedUserResponse.From).ToArray(),
            ready.Unmatched.Select(SteamUnmatchedFriendResponse.From).ToArray(),
            ready.InvitationUrl),
        SteamImportPrivateProfile profile => new PrivateProfileSteamFriendImportResponse(
            profile.Matches.Select(SteamMatchedUserResponse.From).ToArray(),
            profile.Unmatched.Select(SteamUnmatchedFriendResponse.From).ToArray(),
            profile.InvitationUrl),
        SteamImportZeroFriends zeroFriends => new ZeroFriendsSteamFriendImportResponse(
            zeroFriends.Matches.Select(SteamMatchedUserResponse.From).ToArray(),
            zeroFriends.Unmatched.Select(SteamUnmatchedFriendResponse.From).ToArray(),
            zeroFriends.InvitationUrl),
        SteamImportZeroMatches zeroMatches => new ZeroMatchesSteamFriendImportResponse(
            zeroMatches.Matches.Select(SteamMatchedUserResponse.From).ToArray(),
            zeroMatches.Unmatched.Select(SteamUnmatchedFriendResponse.From).ToArray(),
            zeroMatches.InvitationUrl),
        SteamImportNotConfigured notConfigured =>
            new NotConfiguredSteamFriendImportResponse(notConfigured.InvitationUrl),
        SteamImportUpstreamFailure failure =>
            new UpstreamFailureSteamFriendImportResponse(
                failure.Retryable,
                failure.InvitationUrl),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReadySteamFriendImportResponse(
    IReadOnlyList<SteamMatchedUserResponse> Matches,
    IReadOnlyList<SteamUnmatchedFriendResponse> Unmatched,
    string InvitationUrl) : SteamFriendImportResponse;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PrivateProfileSteamFriendImportResponse(
    IReadOnlyList<SteamMatchedUserResponse> Matches,
    IReadOnlyList<SteamUnmatchedFriendResponse> Unmatched,
    string InvitationUrl) : SteamFriendImportResponse;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ZeroFriendsSteamFriendImportResponse(
    IReadOnlyList<SteamMatchedUserResponse> Matches,
    IReadOnlyList<SteamUnmatchedFriendResponse> Unmatched,
    string InvitationUrl) : SteamFriendImportResponse;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ZeroMatchesSteamFriendImportResponse(
    IReadOnlyList<SteamMatchedUserResponse> Matches,
    IReadOnlyList<SteamUnmatchedFriendResponse> Unmatched,
    string InvitationUrl) : SteamFriendImportResponse;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NotConfiguredSteamFriendImportResponse(
    string InvitationUrl) : SteamFriendImportResponse;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpstreamFailureSteamFriendImportResponse(
    bool Retryable,
    string InvitationUrl) : SteamFriendImportResponse;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SteamMatchedUserResponse(Guid UserId, string Username, AvatarResponse Avatar)
{
    public static SteamMatchedUserResponse From(SteamMatchedUser user) =>
        new(user.UserId.Value, user.Username, AvatarResponse.From(user.Avatar));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SteamUnmatchedFriendResponse(
    string SteamId64,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PersonaName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AvatarUrl,
    string InvitationUrl)
{
    public static SteamUnmatchedFriendResponse From(SteamUnmatchedFriend friend) =>
        new(
            friend.SteamId64,
            friend.PersonaName,
            friend.AvatarUrl,
            friend.InvitationUrl);
}
