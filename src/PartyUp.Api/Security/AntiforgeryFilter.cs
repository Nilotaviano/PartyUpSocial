using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace PartyUp.Api.Security;

public sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
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
