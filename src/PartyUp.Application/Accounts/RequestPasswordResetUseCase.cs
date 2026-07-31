using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Accounts;

public sealed class RequestPasswordResetUseCase(
    IUserAccountGateway accounts,
    IUserRepository users,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher,
    AccountEmailOptions emailOptions)
{
    private static readonly Duration ResetTtl = Duration.FromHours(1);

    public async Task ExecuteAsync(string email, CancellationToken cancellationToken)
    {
        string address = AccountInput.Email(email);
        string normalizedEmail = AccountInput.NormalizeEmail(address);
        UserId? userId = await accounts.FindUserIdByEmailAsync(address, cancellationToken);
        IssuedToken issued = await unitOfWork.ExecuteAsync(
            ct => tokens.IssueAsync(
                new IssueTokenRequest(
                    AuthTokenPurpose.PasswordReset,
                    userId,
                    normalizedEmail,
                    ResetTtl),
                ct),
            cancellationToken);

        UserId lookupId = userId ?? UserId.From(Guid.Empty);
        PartyUp.Domain.Users.User? account = await users.FindAsync(
            lookupId,
            cancellationToken);
        PartyUp.Domain.Users.Locale locale = AccountInput.Locale(null);
        if (account is { Locale: { } accountLocale })
        {
            locale = accountLocale;
        }

        EmailTemplate template = userId is null
            ? EmailTemplate.PasswordResetNoAccount
            : EmailTemplate.PasswordReset;
        IReadOnlyDictionary<string, string> model = userId is null
            ? AccountInput.EmptyModel
            : AccountInput.LinkModel(emailOptions.PublicBaseUrl, issued.RawToken);
        await emailDispatcher.DispatchAsync(
            new EmailMessage(address, locale, template, model),
            cancellationToken);
    }
}
