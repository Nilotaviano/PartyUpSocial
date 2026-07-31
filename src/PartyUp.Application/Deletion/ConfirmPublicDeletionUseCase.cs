using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;

namespace PartyUp.Application.Deletion;

public sealed class ConfirmPublicDeletionUseCase(
    ISecurityTokenService tokens,
    RequestAccountDeletionUseCase requestDeletion,
    IUnitOfWork unitOfWork)
{
    public async Task<AccountDeletionRequestResult> ExecuteAsync(
        string rawToken,
        CancellationToken cancellationToken)
    {
        DeletionStart start = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                TokenRedemption redemption = await tokens.RedeemAsync(
                    new RedeemTokenRequest(
                        AuthTokenPurpose.DeletionRequestConfirmation,
                        rawToken),
                    ct);
                if (!redemption.Succeeded || redemption.UserId is not { } userId)
                {
                    return DeletionStart.Failed("invalid_token");
                }

                return await requestDeletion.StartInsideTransactionAsync(userId, ct);
            },
            cancellationToken);

        await requestDeletion.DispatchNotificationAsync(start, cancellationToken);
        return start.Succeeded
            ? AccountDeletionRequestResult.Accepted(start.GraceEndsAt!.Value)
            : AccountDeletionRequestResult.Failed(start.Code!);
    }
}
