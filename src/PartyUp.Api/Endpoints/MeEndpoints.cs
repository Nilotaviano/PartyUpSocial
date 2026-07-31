using System.Text.Json;
using PartyUp.Api.Auth;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Profile;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Api.Endpoints;

public static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder api)
    {
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
        UnlinkPlatformResult platformResult = await unlinkPlatform.ExecuteAsync(
            caller.Id,
            linkId,
            cancellationToken);
        if (platformResult == UnlinkPlatformResult.NotFound)
        {
            return Problem(context, StatusCodes.Status404NotFound, "not_found");
        }

        if (platformResult == UnlinkPlatformResult.Removed)
        {
            return Results.NoContent();
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
