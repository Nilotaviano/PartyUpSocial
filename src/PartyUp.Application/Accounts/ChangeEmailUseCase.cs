using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Accounts;

public sealed class ChangeEmailUseCase(
    IUserAccountGateway accounts,
    IUserRepository users,
    IAccountSessionService sessions,
    ReauthenticateUseCase reauthenticate,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher,
    AccountEmailOptions emailOptions)
{
    private static readonly Duration ChangeTtl = Duration.FromHours(1);

    public async Task<AccountMutationResult> RequestAsync(
        UserId userId,
        string newEmail,
        string? currentPassword,
        string? reauthenticationToken,
        CancellationToken cancellationToken)
    {
        string address = AccountInput.Email(newEmail);
        AccountEmailInfo? previous = await accounts.GetEmailAsync(userId, cancellationToken);
        IssuedToken? issued = null;
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

                issued = await tokens.IssueAsync(
                    new IssueTokenRequest(
                        AuthTokenPurpose.EmailChange,
                        userId,
                        AccountInput.NormalizeEmail(address),
                        ChangeTtl),
                    ct);
                return AccountMutationResult.Success;
            },
            cancellationToken);
        if (!result.Succeeded)
        {
            return result;
        }

        Locale locale = (await users.FindAsync(userId, cancellationToken))?.Locale
            ?? AccountInput.Locale(null);
        if (previous is not null)
        {
            await emailDispatcher.DispatchAsync(
                new EmailMessage(
                    previous.Email,
                    locale,
                    EmailTemplate.EmailChangeRequested,
                    new Dictionary<string, string> { ["NewEmail"] = address }),
                cancellationToken);
        }

        await emailDispatcher.DispatchAsync(
            new EmailMessage(
                address,
                locale,
                EmailTemplate.EmailChangeVerification,
                AccountInput.LinkModel(emailOptions.PublicBaseUrl, issued!.RawToken)),
            cancellationToken);
        return result;
    }

    public Task<AccountMutationResult> VerifyAsync(
        string rawToken,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                TokenRedemption redemption = await tokens.RedeemAsync(
                    new RedeemTokenRequest(AuthTokenPurpose.EmailChange, rawToken),
                    ct);
                if (!redemption.Succeeded ||
                    redemption.UserId is not { } userId ||
                    redemption.EmailNormalized is not { } address)
                {
                    return AccountMutationResult.Failed("invalid_or_expired_token");
                }

                UserId? owner = await accounts.FindUserIdByEmailAsync(address, ct);
                if (owner is not null && owner != userId)
                {
                    return AccountMutationResult.Failed("email_unavailable");
                }

                await accounts.SetEmailAsync(userId, address, confirmed: true, ct);
                await sessions.SignOutEverywhereAsync(userId, ct);
                return AccountMutationResult.Success;
            },
            cancellationToken);
}
