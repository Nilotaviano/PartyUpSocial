using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

public interface IUserAccountGateway
{
    Task<UserId?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken);

    Task<UserId?> FindUserIdByLoginAsync(
        string provider,
        string providerKey,
        CancellationToken cancellationToken);

    Task CreateAccountAsync(
        UserId id,
        string? email,
        bool emailConfirmed,
        string? password,
        CancellationToken cancellationToken);

    Task<PasswordCheckResult> CheckPasswordAsync(
        UserId id,
        string password,
        CancellationToken cancellationToken);

    Task VerifyDummyPasswordAsync(CancellationToken cancellationToken);

    Task SetPasswordAsync(UserId id, string password, CancellationToken cancellationToken);

    Task RemovePasswordAsync(UserId id, CancellationToken cancellationToken);

    Task SetEmailAsync(
        UserId id,
        string email,
        bool confirmed,
        CancellationToken cancellationToken);

    Task ConfirmEmailAsync(UserId id, CancellationToken cancellationToken);

    Task<AccountEmailInfo?> GetEmailAsync(UserId id, CancellationToken cancellationToken);

    Task AddLoginAsync(
        UserId id,
        string provider,
        string providerKey,
        CancellationToken cancellationToken);

    Task RemoveLoginAsync(UserId id, string provider, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListLoginProvidersAsync(
        UserId id,
        CancellationToken cancellationToken);

    Task<bool> HasPasswordAsync(UserId id, CancellationToken cancellationToken);

    Task UpsertSteamIdentityAsync(
        UserId id,
        string steamId64,
        string personaName,
        string? avatarUrl,
        Instant fetchedAt,
        CancellationToken cancellationToken);

    Task RemoveSteamIdentityAsync(UserId id, CancellationToken cancellationToken);

    Task PurgeCredentialsAsync(UserId id, CancellationToken cancellationToken);

    Task EnsureRoleExistsAsync(string role, CancellationToken cancellationToken);

    Task<bool> AddToRoleAsync(
        UserId id,
        string role,
        CancellationToken cancellationToken);
}

public sealed record PasswordCheckResult(bool Succeeded, bool IsLockedOut, bool HashVerified);

public sealed record AccountEmailInfo(string Email, bool IsConfirmed);
