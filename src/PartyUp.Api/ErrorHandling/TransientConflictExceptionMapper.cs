using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PartyUp.Api.ErrorHandling;

/// <summary>
/// A-069's exhaustion arm. <c>UnitOfWork.ExecuteAsync</c> retries a serialization failure
/// (<c>40001</c>), a deadlock (<c>40P01</c>), or a lock timeout (<c>55P03</c>) in a fresh
/// transaction up to <c>MaxAttempts</c>; whatever survives that ladder is contention the server
/// could not resolve, not a defect, so it answers <c>503 transient_conflict</c> with
/// <c>Retry-After: 1</c> rather than the <c>500</c> an unmapped <see cref="PostgresException"/>
/// would otherwise reach. Registered ahead of <see cref="InternalExceptionMapper"/>, and
/// deliberately not scoped to one endpoint family: every path that takes a row lock can exhaust
/// the same ladder.
/// </summary>
public sealed class TransientConflictExceptionMapper : IExceptionHandler
{
    private const string RetryAfterSeconds = "1";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!IsTransientConflict(exception))
        {
            return false;
        }

        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;
        await PartyUpProblemResults.WriteAsync(
            httpContext,
            StatusCodes.Status503ServiceUnavailable,
            "transient_conflict",
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Matches both shapes <c>UnitOfWork</c> retries on: an EF-wrapped failure from
    /// <c>SaveChanges</c>, and a bare <see cref="PostgresException"/> from the raw lock SQL that
    /// never reaches <c>SaveChanges</c>.
    /// </summary>
    private static bool IsTransientConflict(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: PostgresException inner } => IsTransientSqlState(inner),
        PostgresException postgres => IsTransientSqlState(postgres),
        _ => false,
    };

    private static bool IsTransientSqlState(PostgresException exception) =>
        exception.SqlState is PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.LockNotAvailable;
}
