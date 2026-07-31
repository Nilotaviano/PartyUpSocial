using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Accounts;

public sealed class RequestMagicLinkUseCase(
    IUserAccountGateway accounts,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher,
    AccountEmailOptions emailOptions)
{
    private static readonly Duration MagicLinkTtl = Duration.FromMinutes(15);

    public async Task ExecuteAsync(
        string email,
        string? locale,
        CancellationToken cancellationToken)
    {
        string address = AccountInput.Email(email);
        string normalizedEmail = AccountInput.NormalizeEmail(address);
        Locale recipientLocale = AccountInput.Locale(locale);
        UserId? userId = await accounts.FindUserIdByEmailAsync(address, cancellationToken);
        IssuedToken issued = await unitOfWork.ExecuteAsync(
            ct => tokens.IssueAsync(
                new IssueTokenRequest(
                    AuthTokenPurpose.MagicLink,
                    userId,
                    normalizedEmail,
                    MagicLinkTtl,
                    userId is null
                        ? new PendingRegistration(recipientLocale.Value, false)
                        : null),
                ct),
            cancellationToken);

        await emailDispatcher.DispatchAsync(
            new EmailMessage(
                address,
                recipientLocale,
                EmailTemplate.MagicLink,
                AccountInput.LinkModel(emailOptions.PublicBaseUrl, issued.RawToken)),
            cancellationToken);
    }
}
