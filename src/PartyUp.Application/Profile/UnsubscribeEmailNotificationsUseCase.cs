using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

/// <summary>
/// D189_DESIGN.md §3: redeems an <see cref="AuthTokenPurpose.EmailUnsubscribe"/> token. Redemption
/// bulk-consumes every outstanding unsubscribe token for the account (<c>SecurityTokenService</c>),
/// so a dead token here means either an already-processed unsubscribe or an expired/forged one —
/// both are indistinguishable to the caller by design (A-064 posture; the endpoint answers 204
/// either way). A tombstoned user's token still redeems but the disable is skipped as a no-op.
/// </summary>
public sealed class UnsubscribeEmailNotificationsUseCase(
    ISecurityTokenService tokens,
    IUserRepository users,
    IUnitOfWork unitOfWork)
{
    public Task<bool> ExecuteAsync(string rawToken, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                TokenRedemption redemption = await tokens.RedeemAsync(
                    new RedeemTokenRequest(AuthTokenPurpose.EmailUnsubscribe, rawToken),
                    ct);
                if (!redemption.Succeeded || redemption.UserId is not { } userId)
                {
                    return false;
                }

                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null || user.IsTombstoned)
                {
                    return false;
                }

                user.DisableEmailNotificationsForUnsubscribe();
                return true;
            },
            cancellationToken);
}
