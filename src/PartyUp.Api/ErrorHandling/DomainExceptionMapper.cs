using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.ErrorHandling;

public sealed class DomainExceptionMapper : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DomainException domainException)
        {
            return false;
        }

        int status = StatusFor(domainException.Error);
        var problem = new ProblemDetails
        {
            Type = "about:blank",
            Title = TitleFor(status),
            Status = status,
            Detail = domainException.Message,
            Instance = httpContext.Request.Path,
        };
        problem.Extensions["code"] = CodeFor(domainException.Error);
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }

    public static int StatusFor(DomainError error) => error switch
    {
        DomainError.ValidationFailed or DomainError.RangeTooLarge =>
            StatusCodes.Status400BadRequest,
        DomainError.NotFound => StatusCodes.Status404NotFound,
        DomainError.Forbidden or DomainError.Blocked => StatusCodes.Status403Forbidden,
        DomainError.Duplicate or
        DomainError.Conflict or
        DomainError.CooldownActive or
        DomainError.NameReserved or
        DomainError.WindowClosed or
        DomainError.InvalidTransition or
        DomainError.CapacityExceeded or
        DomainError.TerminalState or
        DomainError.LimitExceeded or
        DomainError.RemovalIsPermanent => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status409Conflict,
    };

    public static string CodeFor(DomainError error)
    {
        string name = error.ToString();
        var code = new System.Text.StringBuilder(name.Length + 4);
        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (index > 0 && char.IsUpper(character))
            {
                code.Append('_');
            }

            code.Append(char.ToLowerInvariant(character));
        }

        return code.ToString();
    }

    private static string TitleFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status409Conflict => "Conflict",
        _ => "Request Failed",
    };
}
