using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

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

        var problem = new ProblemDetails
        {
            Type = "about:blank",
            Title = "Bad Request",
            Status = badRequest.StatusCode,
            Detail = "The request body could not be read.",
            Instance = httpContext.Request.Path,
        };
        problem.Extensions["code"] = "invalid_request";
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = badRequest.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: "application/problem+json",
            cancellationToken);
        return true;
    }
}
