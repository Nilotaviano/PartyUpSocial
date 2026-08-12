using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Accounts;

public sealed class UnlinkAuthMethodUseCase(
    IUserRepository users,
    IUserAccountGateway accounts,
    IAccountSessionService sessions,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher,
    UnlinkSteamUseCase unlinkSteam)
{
    public async Task<UnlinkAuthMethodResult> ExecuteAsync(
        UnlinkAuthMethodCommand command,
        CancellationToken cancellationToken)
    {
        string kind = Canonicalize(command.Kind);
        if (!command.Reauthenticated)
        {
            return UnlinkAuthMethodResult.Failed("reauth_required");
        }

        if (kind == "magicLink")
        {
            return UnlinkAuthMethodResult.Failed("method_not_removable");
        }

        if (kind is not ("password" or "google" or "steam"))
        {
            return UnlinkAuthMethodResult.Failed("invalid_request");
        }

        if (kind == "steam")
        {
            UnlinkSteamResult steamResult = await unlinkSteam.ExecuteAsync(
                new UnlinkSteamCommand(command.UserId, UnlinkOrigin.AuthMethod, true),
                cancellationToken);
            return steamResult.Succeeded
                ? UnlinkAuthMethodResult.Completed
                : UnlinkAuthMethodResult.Failed(steamResult.Code!);
        }

        UnlinkTransactionResult transactionResult = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(command.UserId, ct);
                if (user is null)
                {
                    return UnlinkTransactionResult.Failed("not_found");
                }

                IReadOnlyList<string> providers = await accounts.ListLoginProvidersAsync(
                    command.UserId,
                    ct);
                bool hasPassword = await accounts.HasPasswordAsync(command.UserId, ct);
                AccountEmailInfo? email = await accounts.GetEmailAsync(command.UserId, ct);
                bool linked = kind == "password"
                    ? hasPassword
                    : providers.Contains(ExternalProviderPolicy.Google, StringComparer.OrdinalIgnoreCase);
                if (!linked)
                {
                    return UnlinkTransactionResult.Failed("not_found");
                }

                int remaining = providers.Count(provider =>
                    !string.Equals(provider, kind, StringComparison.OrdinalIgnoreCase));
                if (kind != "password" && hasPassword)
                {
                    remaining++;
                }

                if (email is { IsConfirmed: true })
                {
                    remaining++;
                }

                if (remaining == 0)
                {
                    return UnlinkTransactionResult.Failed("last_auth_method");
                }

                if (kind == "password")
                {
                    await accounts.RemovePasswordAsync(command.UserId, ct);
                }
                else
                {
                    await accounts.RemoveLoginAsync(
                        command.UserId,
                        ExternalProviderPolicy.Google,
                        ct);

                    // AVATARS_DESIGN.md §3.4: unlike Steam (UnlinkSteamUseCase -> User.UnlinkPlatform),
                    // this branch never otherwise touches the aggregate -- without this, a
                    // Google-sourced avatar would dangle after the login is removed.
                    AvatarSource? googleSource = user.AvatarSources.SingleOrDefault(
                        source => source.Kind == AvatarSourceKind.GoogleAccount);
                    if (googleSource is not null)
                    {
                        user.RemoveAvatarSource(googleSource.Id);
                    }
                }

                await sessions.SignOutEverywhereAsync(command.UserId, ct);
                return UnlinkTransactionResult.Completed(email, user.Locale);
            },
            cancellationToken);

        if (!transactionResult.Succeeded)
        {
            return UnlinkAuthMethodResult.Failed(transactionResult.Code!);
        }

        if (transactionResult.Email is { IsConfirmed: true } email)
        {
            await emailDispatcher.DispatchAsync(
                new EmailMessage(
                    email.Email,
                    transactionResult.Locale ?? AccountInput.Locale(null),
                    EmailTemplate.AuthMethodRemoved,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Method"] = kind,
                    }),
                cancellationToken);
        }

        return UnlinkAuthMethodResult.Completed;
    }

    private static string Canonicalize(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "magiclink" => "magicLink",
        "password" => "password",
        "google" => "google",
        "steam" => "steam",
        _ => string.Empty,
    };

    private sealed record UnlinkTransactionResult(
        bool Succeeded,
        string? Code,
        AccountEmailInfo? Email,
        Locale? Locale)
    {
        public static UnlinkTransactionResult Completed(AccountEmailInfo? email, Locale? locale) =>
            new(true, null, email, locale);

        public static UnlinkTransactionResult Failed(string code) => new(false, code, null, null);
    }
}

public sealed record UnlinkAuthMethodCommand(UserId UserId, string Kind, bool Reauthenticated);

public sealed record UnlinkAuthMethodResult(bool Succeeded, string? Code)
{
    public static UnlinkAuthMethodResult Completed { get; } = new(true, null);

    public static UnlinkAuthMethodResult Failed(string code) => new(false, code);
}
