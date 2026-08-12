using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Infrastructure.Persistence;

public sealed class CurrentUserProjectionReader(AppDbContext context)
    : ICurrentUserProjectionReader
{
    public async Task<CurrentUserProjection?> ReadAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        var user = await context.DomainUsers
            .AsNoTracking()
            .Where(user => user.Id == id)
            .Select(user => new
            {
                user.Id,
                user.Lifecycle,
                IsBanned = user.BannedAt != null,
                user.GraceEndsAt,
                HasUsername = user.Username != null,
                user.Locale,
                LastSeenAt = EF.Property<Instant?>(user, UserActivityWriter.LastSeenProperty),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return null;
        }

        var authUser = await context.AuthUsers
            .AsNoTracking()
            .Where(authUser => authUser.Id == id.Value)
            .Select(authUser => new
            {
                authUser.SecurityStamp,
                IsAdmin =
                    (from userRole in context.Set<IdentityUserRole<Guid>>()
                     join role in context.Set<AuthRole>() on userRole.RoleId equals role.Id
                     where userRole.UserId == authUser.Id && role.NormalizedName == "ADMIN"
                     select role.Id).Any(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (authUser is null)
        {
            return null;
        }

        return new CurrentUserProjection(
            user.Id,
            user.Lifecycle,
            user.IsBanned,
            user.GraceEndsAt,
            authUser.IsAdmin,
            user.HasUsername,
            user.Locale,
            authUser.SecurityStamp ?? string.Empty,
            user.LastSeenAt);
    }
}
