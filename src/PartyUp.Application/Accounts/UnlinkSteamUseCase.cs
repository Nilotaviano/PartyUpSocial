using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Accounts;

public sealed class UnlinkSteamUseCase(
    IUserRepository users,
    IUserAccountGateway accounts,
    IAccountSessionService sessions,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher)
{
    public async Task<UnlinkSteamResult> ExecuteAsync(
        UnlinkSteamCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.Reauthenticated)
        {
            return UnlinkSteamResult.Failed("reauth_required");
        }

        UnlinkSteamResult result = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(command.UserId, ct);
                if (user is null)
                {
                    return UnlinkSteamResult.Failed("not_found");
                }

                PlatformLink? link = user.PlatformLinks.SingleOrDefault(value =>
                    value.Platform == Platform.Steam &&
                    (command.PlatformLinkId is null || value.Id == command.PlatformLinkId));
                if (link is null)
                {
                    return UnlinkSteamResult.Failed("not_found");
                }

                IReadOnlyList<string> providers = await accounts.ListLoginProvidersAsync(
                    command.UserId,
                    ct);
                int remainingMethods = providers.Count(provider =>
                    !string.Equals(provider, ExternalProviderPolicy.Steam, StringComparison.OrdinalIgnoreCase));
                if (await accounts.HasPasswordAsync(command.UserId, ct))
                {
                    remainingMethods++;
                }

                AccountEmailInfo? email = await accounts.GetEmailAsync(command.UserId, ct);
                if (email is { IsConfirmed: true })
                {
                    remainingMethods++;
                }

                if (remainingMethods == 0)
                {
                    return UnlinkSteamResult.Failed("last_auth_method");
                }

                user.UnlinkPlatform(link.Id);
                await accounts.RemoveLoginAsync(
                    command.UserId,
                    ExternalProviderPolicy.Steam,
                    ct);
                await accounts.RemoveSteamIdentityAsync(command.UserId, ct);
                await sessions.SignOutEverywhereAsync(command.UserId, ct);
                return UnlinkSteamResult.Completed(email, user.Locale);
            },
            cancellationToken);

        if (result.Succeeded && result.Email is { IsConfirmed: true } email)
        {
            await emailDispatcher.DispatchAsync(
                new EmailMessage(
                    email.Email,
                    result.Locale ?? AccountInput.Locale(null),
                    EmailTemplate.AuthMethodRemoved,
                    AccountInput.EmptyModel),
                cancellationToken);
        }

        return result;
    }
}

public enum UnlinkOrigin
{
    AuthMethod,
    PlatformLink,
}

public sealed record UnlinkSteamCommand(
    UserId UserId,
    UnlinkOrigin Origin,
    bool Reauthenticated,
    PlatformLinkId? PlatformLinkId = null);

public sealed record UnlinkSteamResult(
    bool Succeeded,
    string? Code,
    AccountEmailInfo? Email,
    Locale? Locale)
{
    public static UnlinkSteamResult Completed(AccountEmailInfo? email, Locale? locale) =>
        new(true, null, email, locale);

    public static UnlinkSteamResult Failed(string code) => new(false, code, null, null);
}
