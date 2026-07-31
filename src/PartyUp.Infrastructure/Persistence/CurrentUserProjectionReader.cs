using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Infrastructure.Persistence;

public sealed class CurrentUserProjectionReader(AppDbContext context)
    : ICurrentUserProjectionReader
{
    public Task<CurrentUserProjection?> ReadAsync(
        UserId id,
        CancellationToken cancellationToken) =>
        (
            from user in context.DomainUsers.AsNoTracking()
            join authUser in context.AuthUsers.AsNoTracking() on user.Id.Value equals authUser.Id
            let isAdmin =
                (from userRole in context.Set<IdentityUserRole<Guid>>()
                 join role in context.Set<AuthRole>() on userRole.RoleId equals role.Id
                 where userRole.UserId == authUser.Id && role.NormalizedName == "ADMIN"
                 select role.Id).Any()
            where user.Id == id
            select new CurrentUserProjection(
                user.Id,
                user.Lifecycle,
                user.BannedAt != null,
                user.GraceEndsAt,
                isAdmin,
                user.Username != null,
                user.Locale,
                authUser.SecurityStamp ?? string.Empty))
        .SingleOrDefaultAsync(cancellationToken);
}
