using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using PartyUp.Application.Accounts;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Api.Auth;

public sealed class AuthenticateAndSignIn(
    PostAuthenticationGateUseCase postAuthenticationGate,
    UserManager<AuthUser> userManager,
    IUserClaimsPrincipalFactory<AuthUser> principalFactory,
    IHttpContextAccessor httpContextAccessor,
    IUserAccountGateway accounts,
    IUserRepository users,
    IEmailDispatcher emailDispatcher)
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

        if (admitted.DeletionWasCanceled &&
            await accounts.GetEmailAsync(userId, cancellationToken) is
                { IsConfirmed: true } email)
        {
            Locale locale = (await users.FindAsync(userId, cancellationToken))?.Locale
                ?? AccountInput.Locale(null);
            await emailDispatcher.DispatchAsync(
                new EmailMessage(
                    email.Email,
                    locale,
                    EmailTemplate.DeletionCanceled,
                    AccountInput.EmptyModel),
                cancellationToken);
        }

        return result;
    }
}
