using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace PartyUp.Infrastructure.Identity;

public sealed class PartyUpUserClaimsPrincipalFactory(
    UserManager<AuthUser> userManager,
    IOptions<IdentityOptions> identityOptions)
    : IUserClaimsPrincipalFactory<AuthUser>
{
    // Infrastructure has no ASP.NET FrameworkReference, so this literal cannot use IdentityConstants;
    // it must remain equal to IdentityConstants.ApplicationScheme configured in PartyUp.Api.
    private const string ApplicationScheme = "Identity.Application";

    private readonly IdentityOptions _identityOptions = identityOptions.Value;

    public async Task<ClaimsPrincipal> CreateAsync(AuthUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var identity = new ClaimsIdentity(
            ApplicationScheme,
            _identityOptions.ClaimsIdentity.UserNameClaimType,
            _identityOptions.ClaimsIdentity.RoleClaimType);

        identity.AddClaim(new Claim(
            _identityOptions.ClaimsIdentity.UserIdClaimType,
            user.Id.ToString("D")));

        foreach (string role in await userManager.GetRolesAsync(user))
        {
            identity.AddClaim(new Claim(_identityOptions.ClaimsIdentity.RoleClaimType, role));
        }

        identity.AddClaim(new Claim(
            _identityOptions.ClaimsIdentity.SecurityStampClaimType,
            user.SecurityStamp ?? string.Empty));

        return new ClaimsPrincipal(identity);
    }
}
