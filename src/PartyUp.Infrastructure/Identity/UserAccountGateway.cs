using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Auth;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Infrastructure.Identity;

public sealed partial class UserAccountGateway(
    UserManager<AuthUser> userManager,
    RoleManager<AuthRole> roleManager,
    IPasswordHasher<AuthUser> passwordHasher,
    AppDbContext context,
    ILogger<UserAccountGateway> logger) : IUserAccountGateway, ISteamImportAccountGateway
{
    private const int AccessFailureRetryLimit = 5;

    private const string DummyPasswordHash =
        "AQAAAAIAAYagAAAAEAABAgMEBQYHCAkKCwwNDg9flsNYvey4/acBwIXpga+/6h/XAg/rj3e7bLGM5ZnPNg==";

    private static readonly AuthUser DummyUser = new();

    public async Task<UserId?> FindUserIdByEmailAsync(
        string email,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthUser? user = await userManager.FindByEmailAsync(email);
        return user is null ? null : UserId.From(user.Id);
    }

    public async Task<UserId?> FindUserIdByLoginAsync(
        string provider,
        string providerKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthUser? user = await userManager.FindByLoginAsync(provider, providerKey);
        return user is null ? null : UserId.From(user.Id);
    }

    public async Task CreateAccountAsync(
        UserId id,
        string? email,
        bool emailConfirmed,
        string? password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string identityName = id.Value.ToString("D");
        var user = new AuthUser
        {
            Id = id.Value,
            UserName = identityName,
            NormalizedUserName = identityName,
            Email = email,
            EmailConfirmed = emailConfirmed,
            LockoutEnabled = true,
        };

        await context.SaveChangesAsync(cancellationToken);
        IdentityResult result;
        try
        {
            result = password is null
                ? await userManager.CreateAsync(user)
                : await userManager.CreateAsync(user, password);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "ux_auth_users_normalized_email",
            })
        {
            throw new DomainException(
                DomainError.Duplicate,
                "An account already exists for this email address.");
        }
        ThrowIfFailed(result, "create the identity account");
    }

    public async Task<PasswordCheckResult> CheckPasswordAsync(
        UserId id,
        string password,
        CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);

        if (await userManager.IsLockedOutAsync(user))
        {
            return new PasswordCheckResult(false, true, false);
        }

        bool hashVerified = user.PasswordHash is not null;
        bool succeeded = hashVerified && await userManager.CheckPasswordAsync(user, password);

        if (succeeded)
        {
            await ResetAccessFailedCountAsync(user, cancellationToken);
            return new PasswordCheckResult(true, false, true);
        }

        await RecordAccessFailureAsync(user, cancellationToken);
        bool isLockedOut = await userManager.IsLockedOutAsync(user);
        return new PasswordCheckResult(false, isLockedOut, hashVerified);
    }

    public Task VerifyDummyPasswordAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = passwordHasher.VerifyHashedPassword(
            DummyUser,
            DummyPasswordHash,
            "PartyUp non-matching dummy verification input");
        return Task.CompletedTask;
    }

    public async Task SetPasswordAsync(
        UserId id,
        string password,
        CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        user.PasswordHash = passwordHasher.HashPassword(user, password);
        ThrowIfFailed(await userManager.UpdateAsync(user), "set the password");
    }

    public async Task RemovePasswordAsync(UserId id, CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        if (user.PasswordHash is not null)
        {
            ThrowIfFailed(await userManager.RemovePasswordAsync(user), "remove the password");
        }
    }

    public async Task SetEmailAsync(
        UserId id,
        string email,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        ThrowIfFailed(await userManager.SetEmailAsync(user, email), "set the email address");
        user.EmailConfirmed = confirmed;
        ThrowIfFailed(await userManager.UpdateAsync(user), "set the email confirmation state");
    }

    public async Task ConfirmEmailAsync(UserId id, CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        for (int attempt = 1; attempt <= AccessFailureRetryLimit; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (user.EmailConfirmed)
            {
                // A concurrent redemption already confirmed it; D-067's per-token race has one
                // semantic winner, and every loser observes the same confirmed state.
                return;
            }

            user.EmailConfirmed = true;
            IdentityResult result;
            try
            {
                result = await userManager.UpdateAsync(user);
            }
            catch (DbUpdateConcurrencyException)
            {
                await context.Entry(user).ReloadAsync(cancellationToken);
                continue;
            }

            if (result.Succeeded)
            {
                return;
            }

            bool isConcurrencyFailure = result.Errors.Any(error =>
                string.Equals(error.Code, "ConcurrencyFailure", StringComparison.Ordinal));
            if (!isConcurrencyFailure)
            {
                ThrowIfFailed(result, "confirm the email address");
            }

            await context.Entry(user).ReloadAsync(cancellationToken);
        }

        if (!user.EmailConfirmed)
        {
            LogConcurrencyRetryExhausted("confirm the email address", user);
        }
    }

    public async Task<AccountEmailInfo?> GetEmailAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        return user.Email is null
            ? null
            : new AccountEmailInfo(user.Email, user.EmailConfirmed);
    }

    public async Task AddLoginAsync(
        UserId id,
        string provider,
        string providerKey,
        CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        var login = new UserLoginInfo(provider, providerKey, provider);
        ThrowIfFailed(await userManager.AddLoginAsync(user, login), "add the external login");
    }

    public async Task RemoveLoginAsync(
        UserId id,
        string provider,
        CancellationToken cancellationToken)
    {
        // Store-level removal, not UserManager.RemoveLoginAsync: the manager
        // would rotate the security stamp as a hidden side effect. A-049 does
        // require rotation on unlink, but the use case performs it explicitly
        // via SignOutEverywhereAsync so it happens exactly once, at the layer
        // that owns the decision — callers of this method must rotate.
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        List<IdentityUserLogin<Guid>> logins = await context.Set<IdentityUserLogin<Guid>>()
            .Where(login => login.UserId == user.Id && login.LoginProvider == provider)
            .ToListAsync(cancellationToken);
        context.Set<IdentityUserLogin<Guid>>().RemoveRange(logins);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListLoginProvidersAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        IList<UserLoginInfo> logins = await userManager.GetLoginsAsync(user);
        return logins.Select(login => login.LoginProvider).Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task<bool> HasPasswordAsync(
        UserId id,
        CancellationToken cancellationToken) =>
        (await RequireUserAsync(id, cancellationToken)).PasswordHash is not null;

    public async Task UpsertSteamIdentityAsync(
        UserId id,
        string steamId64,
        string personaName,
        Instant fetchedAt,
        CancellationToken cancellationToken)
    {
        SteamIdentity? identity = await context.SteamIdentities.FindAsync([id], cancellationToken);
        if (identity is null)
        {
            context.SteamIdentities.Add(
                new SteamIdentity(id, steamId64, personaName, fetchedAt));
        }
        else
        {
            identity.Update(steamId64, personaName, fetchedAt);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "ux_steam_identities_steam_id64",
            })
        {
            throw new DomainException(
                DomainError.Duplicate,
                "That Steam identity is already linked to another account.");
        }
    }

    public async Task RemoveSteamIdentityAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        SteamIdentity? identity = await context.SteamIdentities.FindAsync([id], cancellationToken);
        if (identity is not null)
        {
            context.SteamIdentities.Remove(identity);
        }
    }

    public Task<string?> FindSteamIdAsync(
        UserId id,
        CancellationToken cancellationToken) =>
        context.SteamIdentities.AsNoTracking()
            .Where(identity => identity.UserId == id)
            .Select(identity => identity.SteamId64)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, UserId>> FindSteamIdentityOwnersAsync(
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(steamIds);
        return await context.SteamIdentities.AsNoTracking()
            .Where(identity => steamIds.Contains(identity.SteamId64))
            .ToDictionaryAsync(
                identity => identity.SteamId64,
                identity => identity.UserId,
                StringComparer.Ordinal,
                cancellationToken);
    }

    public async Task LockUsersAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        Guid[] orderedIds = userIds
            .Select(id => id.Value)
            .Distinct()
            .Order()
            .ToArray();
        if (orderedIds.Length == 0)
        {
            return;
        }

        var parameter = new NpgsqlParameter<Guid[]>("user_ids", orderedIds);
        await context.Database.ExecuteSqlRawAsync(
            """
            SELECT 1
            FROM users
            WHERE id = ANY (@user_ids)
            ORDER BY id
            FOR UPDATE
            """,
            [parameter],
            cancellationToken);
    }

    public async Task<SteamImportOwnershipSnapshot> ReadSteamImportOwnershipSnapshotAsync(
        UserId importerId,
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(steamIds);
        string? importerSteamId = await context.SteamIdentities.AsNoTracking()
            .Where(identity => identity.UserId == importerId)
            .Select(identity => identity.SteamId64)
            .SingleOrDefaultAsync(cancellationToken);

        List<SteamImportCandidateRow> rows = await (
            from identity in context.SteamIdentities.AsNoTracking()
            where steamIds.Contains(identity.SteamId64)
            join candidate in context.DomainUsers.AsNoTracking()
                on identity.UserId equals candidate.Id
            select new SteamImportCandidateRow(
                identity.SteamId64,
                candidate,
                context.Friendships.Where(friendship =>
                        (friendship.UserLoId == importerId &&
                         friendship.UserHiId == candidate.Id) ||
                        (friendship.UserLoId == candidate.Id &&
                         friendship.UserHiId == importerId))
                    .Select(friendship => (FriendshipStatus?)friendship.Status)
                    .SingleOrDefault(),
                context.Blocks.Any(block =>
                    (block.BlockerId == importerId && block.BlockedId == candidate.Id) ||
                    (block.BlockerId == candidate.Id && block.BlockedId == importerId))))
            .ToListAsync(cancellationToken);

        Dictionary<string, SteamImportCandidate> candidates = rows.ToDictionary(
            row => row.SteamId64,
            row => new SteamImportCandidate(
                row.SteamId64,
                row.User,
                row.FriendshipStatus switch
                {
                    FriendshipStatus.Accepted => FriendRelationshipKind.Accepted,
                    FriendshipStatus.Pending => FriendRelationshipKind.OutgoingPending,
                    _ => FriendRelationshipKind.None,
                },
                row.IsBlocked),
            StringComparer.Ordinal);
        return new SteamImportOwnershipSnapshot(importerSteamId, candidates);
    }

    public async Task PurgeCredentialsAsync(UserId id, CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        string? normalizedEmail = user.NormalizedEmail;
        await context.Set<AuthToken>()
            .Where(token =>
                token.UserId == id ||
                normalizedEmail != null && token.EmailNormalized == normalizedEmail)
            .ExecuteDeleteAsync(cancellationToken);
        IList<UserLoginInfo> logins = await userManager.GetLoginsAsync(user);

        foreach (UserLoginInfo login in logins)
        {
            ThrowIfFailed(
                await userManager.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey),
                "purge an external login");
        }

        await context.Set<IdentityUserToken<Guid>>()
            .Where(token => token.UserId == user.Id)
            .ExecuteDeleteAsync(cancellationToken);
        await context.SteamIdentities
            .Where(identity => identity.UserId == id)
            .ExecuteDeleteAsync(cancellationToken);

        user.Email = null;
        user.NormalizedEmail = null;
        user.EmailConfirmed = false;
        user.PasswordHash = null;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;
        user.TwoFactorEnabled = false;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        ThrowIfFailed(await userManager.UpdateAsync(user), "purge identity credentials");
    }

    public async Task EnsureRoleExistsAsync(string role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await roleManager.RoleExistsAsync(role))
        {
            ThrowIfFailed(await roleManager.CreateAsync(new AuthRole(role)), "create the role");
        }
    }

    public async Task<bool> AddToRoleAsync(
        UserId id,
        string role,
        CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        if (await userManager.IsInRoleAsync(user, role))
        {
            return false;
        }

        ThrowIfFailed(await userManager.AddToRoleAsync(user, role), "add the user to the role");
        return true;
    }

    private async Task<AuthUser> RequireUserAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthUser? user = await userManager.FindByIdAsync(id.Value.ToString("D"));
        return user ?? throw new DomainException(DomainError.NotFound, "The identity account was not found.");
    }

    private async Task RecordAccessFailureAsync(
        AuthUser user,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= AccessFailureRetryLimit; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IdentityResult result;
            try
            {
                result = await userManager.AccessFailedAsync(user);
            }
            catch (DbUpdateConcurrencyException)
            {
                await context.Entry(user).ReloadAsync(cancellationToken);
                if (attempt < AccessFailureRetryLimit)
                {
                    continue;
                }

                LogConcurrencyRetryExhausted("record an access failure", user);
                return;
            }

            if (result.Succeeded)
            {
                return;
            }

            bool isConcurrencyFailure = result.Errors.Any(error =>
                string.Equals(error.Code, "ConcurrencyFailure", StringComparison.Ordinal));
            if (!isConcurrencyFailure)
            {
                ThrowIfFailed(result, "record an access failure");
            }

            await context.Entry(user).ReloadAsync(cancellationToken);
        }

        LogConcurrencyRetryExhausted("record an access failure", user);
    }

    private async Task ResetAccessFailedCountAsync(
        AuthUser user,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= AccessFailureRetryLimit; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IdentityResult result;
            try
            {
                result = await userManager.ResetAccessFailedCountAsync(user);
            }
            catch (DbUpdateConcurrencyException)
            {
                await context.Entry(user).ReloadAsync(cancellationToken);
                if (attempt < AccessFailureRetryLimit)
                {
                    continue;
                }

                LogConcurrencyRetryExhausted("reset access failures", user);
                return;
            }

            if (result.Succeeded)
            {
                return;
            }

            bool isConcurrencyFailure = result.Errors.Any(error =>
                string.Equals(error.Code, "ConcurrencyFailure", StringComparison.Ordinal));
            if (!isConcurrencyFailure)
            {
                ThrowIfFailed(result, "reset access failures");
            }

            await context.Entry(user).ReloadAsync(cancellationToken);
        }

        LogConcurrencyRetryExhausted("reset access failures", user);
    }

    private void LogConcurrencyRetryExhausted(string operation, AuthUser user)
    {
        LogConcurrencyRetryExhausted(
            logger,
            operation,
            AccessFailureRetryLimit,
            user.Id);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Identity operation {Operation} exhausted {RetryLimit} concurrency retries for user {UserId}.")]
    private static partial void LogConcurrencyRetryExhausted(
        ILogger logger,
        string operation,
        int retryLimit,
        Guid userId);

    private static void ThrowIfFailed(IdentityResult result, string operation)
    {
        if (result.Succeeded)
        {
            return;
        }

        string details = string.Join("; ", result.Errors.Select(error => error.Description));
        throw new DomainException(
            DomainError.Conflict,
            FormattableString.Invariant($"Identity could not {operation}: {details}"));
    }

    private sealed record SteamImportCandidateRow(
        string SteamId64,
        User User,
        FriendshipStatus? FriendshipStatus,
        bool IsBlocked);
}
