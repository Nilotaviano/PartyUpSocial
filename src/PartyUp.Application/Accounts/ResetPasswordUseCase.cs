using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Accounts;

public sealed class ResetPasswordUseCase(
    IUserAccountGateway accounts,
    IAccountSessionService sessions,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork)
{
    public Task<PasswordResetResult> ExecuteAsync(
        string token,
        string newPassword,
        CancellationToken cancellationToken)
    {
        AccountInput.RequirePassword(newPassword);
        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                TokenRedemption redemption = await tokens.RedeemAsync(
                    new RedeemTokenRequest(AuthTokenPurpose.PasswordReset, token),
                    ct);
                if (!redemption.Succeeded || redemption.UserId is not { } userId)
                {
                    return PasswordResetResult.Failed;
                }

                await accounts.SetPasswordAsync(userId, newPassword, ct);
                await sessions.SignOutEverywhereAsync(userId, ct);
                return PasswordResetResult.Success(userId);
            },
            cancellationToken);
    }
}

public sealed record PasswordResetResult(bool Succeeded, UserId? UserId, string? Code)
{
    public static PasswordResetResult Failed { get; } =
        new(false, null, "invalid_or_expired_token");

    public static PasswordResetResult Success(UserId userId) => new(true, userId, null);
}
