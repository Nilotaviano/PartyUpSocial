using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Accounts;

public sealed class ReauthenticateUseCase(
    IUserAccountGateway accounts,
    IUserRepository users,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher,
    AccountEmailOptions emailOptions)
{
    private static readonly Duration ProofTtl = Duration.FromMinutes(5);

    public async Task<ReauthenticationResult> WithPasswordAsync(
        UserId userId,
        string currentPassword,
        CancellationToken cancellationToken)
    {
        PasswordCheckResult check =
            await accounts.CheckPasswordAsync(userId, currentPassword, cancellationToken);
        if (!check.HashVerified)
        {
            await accounts.VerifyDummyPasswordAsync(cancellationToken);
        }

        if (!check.Succeeded || check.IsLockedOut)
        {
            return ReauthenticationResult.Required;
        }

        IssuedToken proof = await unitOfWork.ExecuteAsync(
            ct => tokens.IssueAsync(
                new IssueTokenRequest(
                    AuthTokenPurpose.Reauthentication,
                    userId,
                    null,
                    ProofTtl),
                ct),
            cancellationToken);
        return ReauthenticationResult.Proven(proof.RawToken);
    }

    public async Task<ReauthenticationResult> RequestEmailAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        AccountEmailInfo? email = await accounts.GetEmailAsync(userId, cancellationToken);
        if (email is not { IsConfirmed: true })
        {
            return ReauthenticationResult.EmailRequired;
        }

        IssuedToken proof = await unitOfWork.ExecuteAsync(
            ct => tokens.IssueAsync(
                new IssueTokenRequest(
                    AuthTokenPurpose.Reauthentication,
                    userId,
                    null,
                    ProofTtl),
                ct),
            cancellationToken);
        Locale locale = (await users.FindAsync(userId, cancellationToken))?.Locale
            ?? AccountInput.Locale(null);
        await emailDispatcher.DispatchAsync(
            new EmailMessage(
                email.Email,
                locale,
                EmailTemplate.ReauthenticationRequested,
                AccountInput.LinkModel(emailOptions.PublicBaseUrl, proof.RawToken)),
            cancellationToken);
        return ReauthenticationResult.Requested;
    }

    public async Task<bool> ValidateProofAsync(
        UserId userId,
        string? currentPassword,
        string? reauthenticationToken,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(currentPassword))
        {
            PasswordCheckResult check =
                await accounts.CheckPasswordAsync(userId, currentPassword, cancellationToken);
            if (!check.HashVerified)
            {
                await accounts.VerifyDummyPasswordAsync(cancellationToken);
            }

            return check.Succeeded && !check.IsLockedOut;
        }

        if (string.IsNullOrWhiteSpace(reauthenticationToken))
        {
            return false;
        }

        TokenRedemption redemption = await tokens.RedeemAsync(
            new RedeemTokenRequest(
                AuthTokenPurpose.Reauthentication,
                reauthenticationToken),
            cancellationToken);
        return redemption.Succeeded && redemption.UserId == userId;
    }
}

public sealed record ReauthenticationResult(
    bool Succeeded,
    bool WasRequested,
    string? Token,
    string? Code)
{
    public static ReauthenticationResult Required { get; } =
        new(false, false, null, "reauth_required");

    public static ReauthenticationResult EmailRequired { get; } =
        new(false, false, null, "email_required");

    public static ReauthenticationResult Requested { get; } =
        new(true, true, null, null);

    public static ReauthenticationResult Proven(string token) =>
        new(true, false, token, null);
}
