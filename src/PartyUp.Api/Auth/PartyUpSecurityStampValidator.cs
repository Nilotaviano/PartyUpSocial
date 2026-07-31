using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace PartyUp.Api.Auth;

public sealed class PartyUpSecurityStampValidator(
    CurrentUserAccessor currentUserAccessor,
    IOptions<IdentityOptions> identityOptions) : ISecurityStampValidator
{
    private readonly IdentityOptions _identityOptions = identityOptions.Value;

    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        string? principalStamp = context.Principal?.FindFirst(
            _identityOptions.ClaimsIdentity.SecurityStampClaimType)?.Value;
        var projection = await currentUserAccessor.GetProjectionAsync(context.HttpContext.RequestAborted);

        if (projection is not null &&
            !string.IsNullOrEmpty(principalStamp) &&
            string.Equals(principalStamp, projection.SecurityStamp, StringComparison.Ordinal))
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
}
