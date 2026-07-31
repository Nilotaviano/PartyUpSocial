using System.Security.Claims;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Auth;

public sealed class CurrentUserAccessor(
    IHttpContextAccessor httpContextAccessor,
    ICurrentUserProjectionReader projectionReader,
    IClock clock) : ICurrentUserAccessor
{
    private Task<CurrentUserProjection?>? _projection;

    public async Task<CurrentUser?> GetAsync(CancellationToken cancellationToken)
    {
        CurrentUserProjection? projection = await GetProjectionAsync(cancellationToken);
        if (projection is null || projection.IsBanned || GraceHasElapsed(projection))
        {
            return null;
        }

        return new CurrentUser(
            projection.Id,
            projection.Lifecycle,
            projection.IsBanned,
            projection.GraceEndsAt,
            projection.IsAdmin,
            projection.IsProfileComplete,
            projection.Locale);
    }

    internal Task<CurrentUserProjection?> GetProjectionAsync(CancellationToken cancellationToken)
    {
        if (_projection is not null)
        {
            return _projection;
        }

        string? subject = httpContextAccessor.HttpContext?.User
            .FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(subject, out Guid value)
            ? GetProjectionAsync(UserId.From(value), cancellationToken)
            : Task.FromResult<CurrentUserProjection?>(null);
    }

    internal Task<CurrentUserProjection?> GetProjectionAsync(
        UserId subject,
        CancellationToken cancellationToken)
    {
        _projection ??= projectionReader.ReadAsync(subject, cancellationToken);
        return _projection;
    }

    private bool GraceHasElapsed(CurrentUserProjection projection) =>
        projection.GraceEndsAt is { } graceEndsAt && clock.GetCurrentInstant() >= graceEndsAt;
}
