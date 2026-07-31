using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Accounts;

public sealed class ConsumeMagicLinkUseCase(
    IUserAccountGateway accounts,
    IUserRepository users,
    IFriendCodeGenerator friendCodes,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<MagicLinkConsumptionResult> ExecuteAsync(
        string token,
        bool ageGateAttested,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                TokenRedemption redemption = await tokens.RedeemAsync(
                    new RedeemTokenRequest(
                        AuthTokenPurpose.MagicLink,
                        token,
                        AgeGateAttested: ageGateAttested),
                    ct);
                if (!redemption.Succeeded)
                {
                    return redemption.Failure == TokenRedemptionFailure.RequiresAgeGate
                        ? MagicLinkConsumptionResult.Failed("age_gate_required")
                        : MagicLinkConsumptionResult.Failed("invalid_or_expired_token");
                }

                UserId? userId = redemption.UserId;
                if (userId is null && redemption.EmailNormalized is { } normalizedAddress)
                {
                    userId = await accounts.FindUserIdByEmailAsync(normalizedAddress, ct);
                    if (userId is null)
                    {
                        UserId createdId = UserId.New();
                        FriendCode friendCode = await friendCodes.NextAsync(ct);
                        User user = User.BeginOnboarding(
                            createdId,
                            friendCode,
                            AgeGateSource.SelfAttested,
                            clock);
                        users.Add(user);
                        await accounts.CreateAccountAsync(
                            createdId,
                            redemption.Email ?? normalizedAddress.ToLowerInvariant(),
                            emailConfirmed: true,
                            password: null,
                            ct);
                        userId = createdId;
                    }
                }

                if (userId is null)
                {
                    return MagicLinkConsumptionResult.Failed("invalid_or_expired_token");
                }

                await accounts.ConfirmEmailAsync(userId.Value, ct);
                return MagicLinkConsumptionResult.Success(userId.Value);
            },
            cancellationToken);
}

public sealed record MagicLinkConsumptionResult(bool Succeeded, UserId? UserId, string? Code)
{
    public static MagicLinkConsumptionResult Success(UserId userId) =>
        new(true, userId, null);

    public static MagicLinkConsumptionResult Failed(string code) =>
        new(false, null, code);
}
