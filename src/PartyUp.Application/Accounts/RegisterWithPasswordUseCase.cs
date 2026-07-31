using System.Net.Mail;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Accounts;

public sealed class RegisterWithPasswordUseCase(
    IUserAccountGateway accounts,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher,
    AccountEmailOptions emailOptions)
{
    private static readonly Duration VerificationTtl = Duration.FromHours(24);

    public async Task<RegistrationResult> ExecuteAsync(
        RegisterWithPasswordCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        string address = AccountInput.Email(command.Email);
        if (!command.AgeGateAttested)
        {
            return RegistrationResult.Rejected("age_gate_required");
        }

        string normalizedEmail = AccountInput.NormalizeEmail(address);
        Locale locale = AccountInput.Locale(command.Locale);
        UserId? existingId = await accounts.FindUserIdByEmailAsync(address, cancellationToken);

        IssuedToken issued = await unitOfWork.ExecuteAsync(
            ct => tokens.IssueAsync(
                new IssueTokenRequest(
                    AuthTokenPurpose.EmailVerification,
                    null,
                    normalizedEmail,
                    VerificationTtl,
                    existingId is null
                        ? new PendingRegistration(locale.Value, true)
                        : null),
                ct),
            cancellationToken);

        EmailTemplate template = existingId is null
            ? EmailTemplate.EmailVerification
            : EmailTemplate.ExistingAccountNotice;
        IReadOnlyDictionary<string, string> model = template == EmailTemplate.EmailVerification
            ? AccountInput.LinkModel(emailOptions.PublicBaseUrl, issued.RawToken)
            : AccountInput.EmptyModel;
        await emailDispatcher.DispatchAsync(
            new EmailMessage(address, locale, template, model),
            cancellationToken);

        return RegistrationResult.Accepted;
    }
}

public sealed record RegisterWithPasswordCommand(
    string Email,
    bool AgeGateAttested,
    string? Locale);

public sealed record RegistrationResult(bool IsAccepted, string? Code)
{
    public static RegistrationResult Accepted { get; } = new(true, null);

    public static RegistrationResult Rejected(string code) => new(false, code);
}

public sealed record AccountEmailOptions(string PublicBaseUrl);

public static class AccountInput
{
    public static IReadOnlyDictionary<string, string> EmptyModel { get; } =
        new Dictionary<string, string>();

    public static string Email(string? value)
    {
        string candidate = value?.Trim() ?? string.Empty;
        if (!MailAddress.TryCreate(candidate, out MailAddress? parsed) ||
            !string.Equals(parsed.Address, candidate, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(DomainError.ValidationFailed, "A valid email is required.");
        }

        return candidate;
    }

    public static string NormalizeEmail(string value) => value.Trim().ToUpperInvariant();

    public static Locale Locale(string? value) =>
        PartyUp.Domain.Users.Locale.Create(
            EmbeddedEmailTemplateRenderer.CanonicalizeLocale(value));

    public static void RequirePassword(string? value)
    {
        if (value is null || value.Length < 12)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                "The password must contain at least 12 characters.");
        }
    }

    public static IReadOnlyDictionary<string, string> LinkModel(
        string publicBaseUrl,
        string token) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PublicBaseUrl"] = publicBaseUrl.TrimEnd('/'),
            ["Token"] = token,
        };
}
