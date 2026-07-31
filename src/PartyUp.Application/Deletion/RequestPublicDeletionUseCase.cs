using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Deletion;

public sealed class RequestPublicDeletionUseCase(
    IUserAccountGateway accounts,
    IUserRepository users,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher,
    AccountEmailOptions emailOptions)
{
    private static readonly Duration ConfirmationTtl = Duration.FromHours(1);

    public async Task ExecuteAsync(
        RequestPublicDeletionCommand command,
        CancellationToken cancellationToken)
    {
        string address = AccountInput.Email(command.Email);
        string normalized = AccountInput.NormalizeEmail(address);
        UserId? userId = await accounts.FindUserIdByEmailAsync(address, cancellationToken);
        IssuedToken issued = await unitOfWork.ExecuteAsync(
            ct => tokens.IssueAsync(
                new IssueTokenRequest(
                    AuthTokenPurpose.DeletionRequestConfirmation,
                    userId,
                    normalized,
                    ConfirmationTtl,
                    RequestIpHash: command.RequestIpHash),
                ct),
            cancellationToken);

        Locale locale = AccountInput.Locale(command.Locale);
        if (userId is { } knownId &&
            await users.FindAsync(knownId, cancellationToken) is { Locale: { } storedLocale })
        {
            locale = storedLocale;
        }

        EmailTemplate template = userId is null
            ? EmailTemplate.DeletionRequestNoAccount
            : EmailTemplate.DeletionRequestConfirmation;
        IReadOnlyDictionary<string, string> model = userId is null
            ? AccountInput.EmptyModel
            : AccountInput.LinkModel(emailOptions.PublicBaseUrl, issued.RawToken);
        await emailDispatcher.DispatchAsync(
            new EmailMessage(address, locale, template, model),
            cancellationToken);
    }
}

public sealed record RequestPublicDeletionCommand(
    string Email,
    string? Locale,
    byte[]? RequestIpHash = null);
