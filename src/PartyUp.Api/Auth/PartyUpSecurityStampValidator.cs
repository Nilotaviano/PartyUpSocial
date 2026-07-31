using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Auth;

public sealed class PartyUpSecurityStampValidator(
    CurrentUserAccessor currentUserAccessor,
    IOptions<IdentityOptions> identityOptions) : ISecurityStampValidator
{
    private readonly IdentityOptions _identityOptions = identityOptions.Value;

    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        string? subject = context.Principal?.FindFirst(
            _identityOptions.ClaimsIdentity.UserIdClaimType)?.Value;
        string? principalStamp = context.Principal?.FindFirst(
            _identityOptions.ClaimsIdentity.SecurityStampClaimType)?.Value;
        var projection = Guid.TryParse(subject, out Guid subjectId)
            ? await currentUserAccessor.GetProjectionAsync(
                UserId.From(subjectId),
                context.HttpContext.RequestAborted)
            : null;

        if (projection is not null &&
            !string.IsNullOrEmpty(principalStamp) &&
            string.Equals(principalStamp, projection.SecurityStamp, StringComparison.Ordinal))
        {
            return;
        }

        context.RejectPrincipal();
        currentUserAccessor.InvalidateProjection();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
}
