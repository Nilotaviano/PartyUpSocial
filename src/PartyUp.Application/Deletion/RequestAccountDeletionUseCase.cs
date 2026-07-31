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
        return DeletionStart.Completed(user.GraceEndsAt!.Value, email, user.Locale);
    }

    internal async Task DispatchNotificationAsync(
        DeletionStart start,
        CancellationToken cancellationToken)
    {
        if (!start.Succeeded || start.Email is not { IsConfirmed: true } email)
        {
            return;
        }

        await emailDispatcher.DispatchAsync(
            new EmailMessage(
                email.Email,
                start.Locale ?? AccountInput.Locale(null),
                EmailTemplate.DeletionRequested,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GraceEndsAt"] = InstantPattern.ExtendedIso.Format(start.GraceEndsAt!.Value),
                }),
            cancellationToken);
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
    Locale? Locale)
{
    public static DeletionStart Completed(
        Instant graceEndsAt,
        AccountEmailInfo? email,
        Locale? locale) => new(true, null, graceEndsAt, email, locale);

    public static DeletionStart Failed(string code) => new(false, code, null, null, null);
}
