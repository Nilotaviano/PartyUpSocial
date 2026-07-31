using Microsoft.AspNetCore.Identity;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Identity;

public sealed class UserAccountGateway(
    UserManager<AuthUser> userManager,
    RoleManager<AuthRole> roleManager,
    IPasswordHasher<AuthUser> passwordHasher,
    AppDbContext context) : IUserAccountGateway
{
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
        IdentityResult result = password is null
            ? await userManager.CreateAsync(user)
            : await userManager.CreateAsync(user, password);
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
            ThrowIfFailed(await userManager.ResetAccessFailedCountAsync(user), "reset access failures");
            return new PasswordCheckResult(true, false, true);
        }

        ThrowIfFailed(await userManager.AccessFailedAsync(user), "record an access failure");
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
        user.EmailConfirmed = true;
        ThrowIfFailed(await userManager.UpdateAsync(user), "confirm the email address");
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
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        IList<UserLoginInfo> logins = await userManager.GetLoginsAsync(user);

        foreach (UserLoginInfo login in logins.Where(login => login.LoginProvider == provider))
        {
            ThrowIfFailed(
                await userManager.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey),
                "remove the external login");
        }
    }

    public async Task<IReadOnlyList<string>> ListLoginProvidersAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        IList<UserLoginInfo> logins = await userManager.GetLoginsAsync(user);
        return logins.Select(login => login.LoginProvider).Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task PurgeCredentialsAsync(UserId id, CancellationToken cancellationToken)
    {
        AuthUser user = await RequireUserAsync(id, cancellationToken);
        IList<UserLoginInfo> logins = await userManager.GetLoginsAsync(user);

        foreach (UserLoginInfo login in logins)
        {
            ThrowIfFailed(
                await userManager.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey),
                "purge an external login");
        }

        user.Email = null;
        user.NormalizedEmail = null;
        user.EmailConfirmed = false;
        user.PasswordHash = null;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;
        user.TwoFactorEnabled = false;
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
}
