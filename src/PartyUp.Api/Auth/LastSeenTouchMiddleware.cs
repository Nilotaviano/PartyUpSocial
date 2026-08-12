using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Auth;

/// <summary>
/// PHASE8_DESIGN.md §2 Q1 / §3 S1: the ratified <c>users.last_seen_at</c> touch, throttled to one
/// write per <see cref="WriteFloor"/> so activity tracking costs no extra read and almost never a
/// write. It piggybacks on the current-user projection the authorization middleware has already
/// resolved for this request and never resolves one itself — an authenticated request to a route
/// with no PartyUp policy therefore adds no query at all, it simply does not touch.
/// </summary>
public sealed class LastSeenTouchMiddleware(
    RequestDelegate next,
    ILogger<LastSeenTouchMiddleware> logger)
{
    public static readonly Duration WriteFloor = Duration.FromMinutes(10);

    private static readonly Action<ILogger, Exception?> LogTouchFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, nameof(LogTouchFailed)),
            "Failed to touch last_seen_at; the request continues unaffected");

    public async Task InvokeAsync(
        HttpContext context,
        CurrentUserAccessor currentUserAccessor,
        IUserActivityWriter activityWriter,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(currentUserAccessor);
        ArgumentNullException.ThrowIfNull(activityWriter);
        ArgumentNullException.ThrowIfNull(clock);

        // The touch stays ahead of the endpoint deliberately: the projection this reads is the one
        // the authorization middleware resolved, and only routes carrying a PartyUp policy have one —
        // an authenticated request to a policy-free route (health, openapi, the SPA fallback) is
        // therefore not counted as activity. That undercount is accepted: every product surface is
        // policy-gated, and reading the projection here must never start a query of its own.
        CurrentUserProjection? projection = currentUserAccessor.LoadedProjection;
        if (projection is not null && !projection.IsBanned)
        {
            Instant now = clock.GetCurrentInstant();
            if (projection.LastSeenAt is not { } lastSeenAt || now - lastSeenAt >= WriteFloor)
            {
                await TouchAsync(context, activityWriter, projection.Id, now);
            }
        }

        await next(context);
    }

    /// <summary>
    /// Activity telemetry is never worth failing a serviceable request over: a transient failure of
    /// the one-row UPDATE is logged and swallowed, and the next request past the write floor retries
    /// it anyway.
    /// </summary>
    private async Task TouchAsync(
        HttpContext context,
        IUserActivityWriter activityWriter,
        UserId userId,
        Instant now)
    {
        try
        {
            await activityWriter.TouchLastSeenAsync(userId, now, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogTouchFailed(logger, exception);
        }
    }
}
