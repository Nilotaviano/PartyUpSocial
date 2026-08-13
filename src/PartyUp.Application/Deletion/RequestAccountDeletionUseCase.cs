using System.Globalization;
using NodaTime;
using NodaTime.Text;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Deletion;

public sealed class RequestAccountDeletionUseCase(
    IUserRepository users,
    IUserAccountGateway accounts,
    IAccountSessionService sessions,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher,
    IClock clock)
{
    public async Task<AccountDeletionRequestResult> ExecuteAsync(
        RequestAccountDeletionCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.Reauthenticated)
        {
            return AccountDeletionRequestResult.Failed("reauth_required");
        }

        DeletionStart start = await unitOfWork.ExecuteAsync(
            ct => StartInsideTransactionAsync(command.UserId, ct),
            cancellationToken);
        await DispatchNotificationAsync(start, cancellationToken);
        return start.Succeeded
            ? AccountDeletionRequestResult.Accepted(start.GraceEndsAt!.Value)
            : AccountDeletionRequestResult.Failed(start.Code!);
    }

    internal async Task<DeletionStart> StartInsideTransactionAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        User? user = await users.FindForUpdateAsync(userId, cancellationToken);
        if (user is null)
        {
            return DeletionStart.Failed("not_found");
        }

        user.RequestDeletion(clock);
        await sessions.SignOutEverywhereAsync(userId, cancellationToken);
        AccountEmailInfo? email = await accounts.GetEmailAsync(userId, cancellationToken);
        return DeletionStart.Completed(
            user.GraceEndsAt!.Value,
            email,
            user.Locale,
            user.TimeZone);
    }

    internal async Task DispatchNotificationAsync(
        DeletionStart start,
        CancellationToken cancellationToken)
    {
        if (!start.Succeeded || start.Email is not { IsConfirmed: true } email)
        {
            return;
        }

        Locale locale = start.Locale ?? AccountInput.Locale(null);
        await emailDispatcher.DispatchAsync(
            new EmailMessage(
                email.Email,
                locale,
                EmailTemplate.DeletionRequested,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GraceEndsAt"] = FormatGraceEndsAt(
                        start.GraceEndsAt!.Value,
                        start.TimeZone ?? DateTimeZone.Utc,
                        locale),
                }),
            cancellationToken);
    }

    private static string FormatGraceEndsAt(
        Instant graceEndsAt,
        DateTimeZone timeZone,
        Locale locale)
    {
        string canonicalLocale = EmbeddedEmailTemplateRenderer.CanonicalizeLocale(locale.Value);
        (string cultureName, string patternText) = canonicalLocale switch
        {
            "pt-BR" => ("pt-BR", "d 'de' MMMM 'de' yyyy '\u00e0s' HH:mm z"),
            "es" => ("es", "d 'de' MMMM 'de' yyyy 'a las' HH:mm z"),
            _ => ("en-US", "MMMM d, yyyy 'at' h:mm tt z"),
        };
        ZonedDateTimePattern pattern = ZonedDateTimePattern.CreateWithInvariantCulture(
            patternText,
            DateTimeZoneProviders.Tzdb)
            .WithCulture(CultureInfo.GetCultureInfo(cultureName));
        return pattern.Format(graceEndsAt.InZone(timeZone));
    }
}

public sealed record RequestAccountDeletionCommand(UserId UserId, bool Reauthenticated);

public sealed record AccountDeletionRequestResult(bool Succeeded, string? Code, Instant? GraceEndsAt)
{
    public static AccountDeletionRequestResult Accepted(Instant graceEndsAt) =>
        new(true, null, graceEndsAt);

    public static AccountDeletionRequestResult Failed(string code) => new(false, code, null);
}

internal sealed record DeletionStart(
    bool Succeeded,
    string? Code,
    Instant? GraceEndsAt,
    AccountEmailInfo? Email,
    Locale? Locale,
    DateTimeZone? TimeZone)
{
    public static DeletionStart Completed(
        Instant graceEndsAt,
        AccountEmailInfo? email,
        Locale? locale,
        DateTimeZone? timeZone) => new(true, null, graceEndsAt, email, locale, timeZone);

    public static DeletionStart Failed(string code) => new(false, code, null, null, null, null);
}
