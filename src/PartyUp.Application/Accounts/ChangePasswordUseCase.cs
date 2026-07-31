using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Accounts;

public sealed class ChangePasswordUseCase(
    IUserAccountGateway accounts,
    IAccountSessionService sessions,
    IUnitOfWork unitOfWork)
{
    public Task<AccountMutationResult> ExecuteAsync(
        UserId userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        AccountInput.RequirePassword(newPassword);
        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                PasswordCheckResult check =
                    await accounts.CheckPasswordAsync(userId, currentPassword, ct);
                if (!check.HashVerified)
                {
                    await accounts.VerifyDummyPasswordAsync(ct);
                }

                if (!check.Succeeded || check.IsLockedOut)
                {
                    return AccountMutationResult.Failed("reauth_required");
                }

                await accounts.SetPasswordAsync(userId, newPassword, ct);
                await sessions.SignOutEverywhereAsync(userId, ct);
                return AccountMutationResult.Success;
            },
            cancellationToken);
    }
}

public sealed record AccountMutationResult(bool Succeeded, string? Code)
{
    public static AccountMutationResult Success { get; } = new(true, null);

    public static AccountMutationResult Failed(string code) => new(false, code);
}
