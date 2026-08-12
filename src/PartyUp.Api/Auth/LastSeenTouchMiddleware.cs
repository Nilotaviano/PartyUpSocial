using NodaTime;
using PartyUp.Application.Abstractions;

namespace PartyUp.Api.Auth;

/// <summary>
/// PHASE8_DESIGN.md §2 Q1 / §3 S1: the ratified <c>users.last_seen_at</c> touch, throttled to one
/// write per <see cref="WriteFloor"/> so activity tracking costs no extra read and almost never a
/// write. It piggybacks on the current-user projection the authorization middleware has already
/// resolved for this request and never resolves one itself — an authenticated request to a route
/// with no PartyUp policy therefore adds no query at all, it simply does not touch.
/// </summary>
public sealed class LastSeenTouchMiddleware(RequestDelegate next)
{
    public static readonly Duration WriteFloor = Duration.FromMinutes(10);

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

        CurrentUserProjection? projection = currentUserAccessor.LoadedProjection;
        if (projection is not null && !projection.IsBanned)
        {
            Instant now = clock.GetCurrentInstant();
            if (projection.LastSeenAt is not { } lastSeenAt || now - lastSeenAt >= WriteFloor)
            {
                await activityWriter.TouchLastSeenAsync(projection.Id, now, context.RequestAborted);
            }
        }

        await next(context);
    }
}
