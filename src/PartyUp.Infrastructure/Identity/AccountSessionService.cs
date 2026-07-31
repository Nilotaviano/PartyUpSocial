using Microsoft.AspNetCore.Identity;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Identity;

public sealed class AccountSessionService(UserManager<AuthUser> userManager)
    : IAccountSessionService
{
    public async Task SignOutEverywhereAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthUser? user = await userManager.FindByIdAsync(id.Value.ToString("D"));

        if (user is null)
        {
            throw new DomainException(DomainError.NotFound, "The identity account was not found.");
        }

        IdentityResult result = await userManager.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            string details = string.Join("; ", result.Errors.Select(error => error.Description));
            throw new DomainException(
                DomainError.Conflict,
                FormattableString.Invariant($"Identity could not rotate the security stamp: {details}"));
        }
    }
}
