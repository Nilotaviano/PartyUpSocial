using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Accounts;

public sealed class VerifyEmailUseCase(
    IUserAccountGateway accounts,
    IUserRepository users,
    IFriendCodeGenerator friendCodes,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<EmailVerificationResult> ExecuteAsync(
        string token,
        string? password,
        bool ageGateAttested,
        CancellationToken cancellationToken)
    {
        if (password is not null)
        {
            AccountInput.RequirePassword(password);
        }
        try
        {
            return await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    TokenRedemption redemption = await tokens.RedeemAsync(
                        new RedeemTokenRequest(
                            AuthTokenPurpose.EmailVerification,
                            token,
                            AgeGateAttested: ageGateAttested),
                        ct);
                    if (!redemption.Succeeded)
                    {
                        return redemption.Failure == TokenRedemptionFailure.RequiresAgeGate
                            ? EmailVerificationResult.Failed("age_gate_required")
                            : EmailVerificationResult.Failed("invalid_or_expired_token");
                    }

                    if (redemption.UserId is { } existingSubject)
                    {
                        await accounts.ConfirmEmailAsync(existingSubject, ct);
                        return EmailVerificationResult.Confirmed(existingSubject);
                    }

                    if (redemption.EmailNormalized is not { } normalizedAddress)
                    {
                        return EmailVerificationResult.Failed("invalid_or_expired_token");
                    }

                    if (password is null)
                    {
                        return EmailVerificationResult.Failed("password_required");
                    }

                    UserId? appeared = await accounts.FindUserIdByEmailAsync(normalizedAddress, ct);
                    if (appeared is not null)
                    {
                        return EmailVerificationResult.Existing;
                    }

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
                        password,
                        ct);
                    return EmailVerificationResult.Created(createdId);
                },
                cancellationToken);
        }
        catch (DomainException exception) when (exception.Error == DomainError.Duplicate)
        {
            return EmailVerificationResult.Existing;
        }
    }
}

public sealed record EmailVerificationResult(
    bool Succeeded,
    UserId? UserId,
    bool ShouldSignIn,
    bool AccountAlreadyExisted,
    string? Code)
{
    public static EmailVerificationResult Created(UserId userId) =>
        new(true, userId, true, false, null);

    public static EmailVerificationResult Confirmed(UserId userId) =>
        new(true, userId, false, false, null);

    public static EmailVerificationResult Existing { get; } =
        new(true, null, false, true, null);

    public static EmailVerificationResult Failed(string code) =>
        new(false, null, false, false, code);
}
