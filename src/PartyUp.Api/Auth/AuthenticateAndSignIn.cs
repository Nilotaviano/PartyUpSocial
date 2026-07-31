using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using PartyUp.Application.Accounts;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Api.Auth;

public sealed class AuthenticateAndSignIn(
    PostAuthenticationGateUseCase postAuthenticationGate,
    UserManager<AuthUser> userManager,
    IUserClaimsPrincipalFactory<AuthUser> principalFactory,
    IHttpContextAccessor httpContextAccessor)
{
    public async Task<PostAuthenticationResult> ExecuteAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        PostAuthenticationResult result =
            await postAuthenticationGate.ExecuteAsync(userId, cancellationToken);

        if (result is not PostAuthenticationResult.Admitted admitted)
        {
            return result;
        }

        AuthUser? authUser = await userManager.FindByIdAsync(admitted.UserId.Value.ToString("D"));
        if (authUser is null)
        {
            return new PostAuthenticationResult.Refused("invalid_credentials");
        }

        var principal = await principalFactory.CreateAsync(authUser);
        HttpContext httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("Authentication requires an active HTTP request.");
        await httpContext.SignInAsync(
            IdentityConstants.ApplicationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });

        return result;
    }
}
