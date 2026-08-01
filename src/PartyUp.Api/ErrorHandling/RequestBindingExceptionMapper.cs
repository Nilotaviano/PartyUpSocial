using Microsoft.AspNetCore.Diagnostics;

namespace PartyUp.Api.ErrorHandling;

public sealed class RequestBindingExceptionMapper : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        int status = badRequest.StatusCode == StatusCodes.Status415UnsupportedMediaType
            ? StatusCodes.Status415UnsupportedMediaType
            : StatusCodes.Status400BadRequest;
        string code = status == StatusCodes.Status415UnsupportedMediaType
            ? "unsupported_media_type"
            : "validation_failed";

        await PartyUpProblemResults.WriteAsync(
            httpContext,
            status,
            code,
            cancellationToken);
        return true;
    }
}

public sealed class RequestContentTypeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        bool requestHasBody =
            context.Request.ContentLength is > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding");

        if (context.GetEndpoint() is not null &&
            context.Request.Path.StartsWithSegments(ApiRoutes.V1) &&
            requestHasBody &&
            !context.Request.HasJsonContentType())
        {
            await PartyUpProblemResults.WriteAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "unsupported_media_type",
                context.RequestAborted);
            return;
        }

        await next(context);
    }
}
