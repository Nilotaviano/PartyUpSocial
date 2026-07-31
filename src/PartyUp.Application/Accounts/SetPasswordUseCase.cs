using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Accounts;

public sealed class SetPasswordUseCase(
    IUserAccountGateway accounts,
    IUserRepository users,
    IAccountSessionService sessions,
    ReauthenticateUseCase reauthenticate,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher)
{
    public async Task<AccountMutationResult> ExecuteAsync(
        UserId userId,
        string newPassword,
        string? currentPassword,
        string? reauthenticationToken,
        CancellationToken cancellationToken)
    {
        AccountInput.RequirePassword(newPassword);
        AccountEmailInfo? email = await accounts.GetEmailAsync(userId, cancellationToken);
        if (email is not { IsConfirmed: true })
        {
            return AccountMutationResult.Failed("email_required");
        }

        AccountMutationResult result = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                bool proven = await reauthenticate.ValidateProofAsync(
                    userId,
                    currentPassword,
                    reauthenticationToken,
                    ct);
                if (!proven)
                {
                    return AccountMutationResult.Failed("reauth_required");
                }

                await accounts.SetPasswordAsync(userId, newPassword, ct);
                await sessions.SignOutEverywhereAsync(userId, ct);
                return AccountMutationResult.Success;
            },
            cancellationToken);
        if (!result.Succeeded)
        {
            return result;
        }

        Locale locale = (await users.FindAsync(userId, cancellationToken))?.Locale
            ?? AccountInput.Locale(null);
        await emailDispatcher.DispatchAsync(
            new EmailMessage(
                email.Email,
                locale,
                EmailTemplate.AuthMethodAdded,
                new Dictionary<string, string> { ["Method"] = "password" }),
            cancellationToken);
        return result;
    }
}
