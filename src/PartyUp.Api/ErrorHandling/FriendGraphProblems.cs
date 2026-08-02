using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace PartyUp.Api.ErrorHandling;

public sealed record PartyUpProblem(
    string Type,
    string Title,
    int Status,
    string Instance,
    string Code,
    string TraceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors = null);

public static class PartyUpProblemResults
{
    public static IResult Create(
        HttpContext context,
        int status,
        string code,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = null) =>
        Results.Json(
            Body(context.Request.Path, context.TraceIdentifier, status, code, errors),
            statusCode: status,
            contentType: "application/problem+json");

    public static async Task WriteAsync(
        HttpContext context,
        int status,
        string code,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.Remove("Location");
        await context.Response.WriteAsJsonAsync(
            Body(context.Request.Path, context.TraceIdentifier, status, code),
            options: null,
            contentType: "application/problem+json",
            cancellationToken);
    }

    public static PartyUpProblem Body(
        PathString instance,
        string traceId,
        int status,
        string code,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = null) =>
        new(
            "about:blank",
            Title(status),
            status,
            instance.Value ?? string.Empty,
            code,
            traceId,
            errors);

    public static string Title(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status415UnsupportedMediaType => "Unsupported Media Type",
        StatusCodes.Status429TooManyRequests => "Too Many Requests",
        StatusCodes.Status500InternalServerError => "Internal Server Error",
        _ => "Request Failed",
    };

    public static RouteHandlerBuilder ProducesPartyUpProblem(
        this RouteHandlerBuilder builder,
        int status)
    {
        builder.ProducesProblem(status);
        builder.Produces<PartyUpProblem>(
            statusCode: status,
            contentType: "application/problem+json");
        return builder;
    }
}

public sealed class InternalExceptionMapper : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        if (!FriendProblemScope.Contains(httpContext))
        {
            return false;
        }

        await PartyUpProblemResults.WriteAsync(
            httpContext,
            StatusCodes.Status500InternalServerError,
            "internal_error",
            cancellationToken);
        return true;
    }
}

public sealed class ProblemAntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        HttpContext httpContext = context.HttpContext;
        if (HttpMethods.IsGet(httpContext.Request.Method) ||
            HttpMethods.IsHead(httpContext.Request.Method) ||
            HttpMethods.IsOptions(httpContext.Request.Method) ||
            HttpMethods.IsTrace(httpContext.Request.Method) ||
            httpContext.GetEndpoint()?.Metadata.GetMetadata<IAntiforgeryMetadata>()
                is { RequiresValidation: false })
        {
            return await next(context);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            if (FriendProblemScope.Contains(httpContext))
            {
                return PartyUpProblemResults.Create(
                    httpContext,
                    StatusCodes.Status400BadRequest,
                    "antiforgery_failed");
            }

            var problem = new ProblemDetails
            {
                Type = "about:blank",
                Title = "Bad Request",
                Status = StatusCodes.Status400BadRequest,
                Detail = "The antiforgery token is invalid.",
                Instance = httpContext.Request.Path,
            };
            problem.Extensions["code"] = "antiforgery_failed";
            problem.Extensions["traceId"] = httpContext.TraceIdentifier;
            return Results.Problem(problem);
        }

        return await next(context);
    }
}

internal static class FriendProblemScope
{
    private static readonly HashSet<string> OperationIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ApiOperationIds.GetFriendShare,
            ApiOperationIds.GetUserProfile,
            ApiOperationIds.ResolveFriendCode,
            ApiOperationIds.ListFriends,
            ApiOperationIds.ListFriendRequests,
            ApiOperationIds.SendFriendRequest,
            ApiOperationIds.AcceptFriendRequest,
            ApiOperationIds.RejectFriendRequest,
            ApiOperationIds.CancelFriendRequest,
            ApiOperationIds.Unfriend,
            ApiOperationIds.ListBlocks,
            ApiOperationIds.BlockUser,
            ApiOperationIds.UnblockUser,
        };

    internal static bool Contains(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
            is { } operationId &&
        OperationIds.Contains(operationId);
}
