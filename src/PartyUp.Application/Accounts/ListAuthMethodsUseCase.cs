using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Accounts;

public sealed class ListAuthMethodsUseCase(IUserAccountGateway accounts)
{
    public async Task<AuthMethodsResult> ExecuteAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        AccountEmailInfo? email = await accounts.GetEmailAsync(userId, cancellationToken);
        bool hasPassword = await accounts.HasPasswordAsync(userId, cancellationToken);
        IReadOnlyList<string> providers = await accounts.ListLoginProvidersAsync(
            userId,
            cancellationToken);
        bool hasMagicLink = email is { IsConfirmed: true };
        string? maskedEmail = email is null ? null : MaskEmail(email.Email);
        bool hasGoogle = Contains(providers, ExternalProviderPolicy.Google);
        bool hasSteam = Contains(providers, ExternalProviderPolicy.Steam);

        return new AuthMethodsResult(
        [
            new AuthMethod("password", hasPassword, hasPassword, maskedEmail),
            new AuthMethod("magicLink", hasMagicLink, false, hasMagicLink ? maskedEmail : null),
            new AuthMethod("google", hasGoogle, hasGoogle, hasGoogle ? maskedEmail : null),
            new AuthMethod("steam", hasSteam, hasSteam, null),
        ]);
    }

    private static bool Contains(IReadOnlyList<string> providers, string provider) =>
        providers.Contains(provider, StringComparer.OrdinalIgnoreCase);

    private static string MaskEmail(string address)
    {
        int separator = address.IndexOf('@', StringComparison.Ordinal);
        return separator <= 0
            ? "***"
            : string.Concat(address.AsSpan(0, 1), "***", address.AsSpan(separator));
    }
}

public sealed record AuthMethod(string Kind, bool Linked, bool Removable, string? Detail);

public sealed record AuthMethodsResult(IReadOnlyList<AuthMethod> Methods);
