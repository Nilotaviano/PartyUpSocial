using System.Text.Json;
using NodaTime.Text;
using PartyUp.Api.Auth;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Deletion;
using PartyUp.Application.Onboarding;
using PartyUp.Application.Profile;
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

    private static IResult ProfileMutationProblem(
        HttpContext context,
        ProfileMutationResult result)
    {
        int status = result.Code == "not_found"
            ? StatusCodes.Status404NotFound
            : result.Code is not null && result.Code.StartsWith("errors.", StringComparison.Ordinal)
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
