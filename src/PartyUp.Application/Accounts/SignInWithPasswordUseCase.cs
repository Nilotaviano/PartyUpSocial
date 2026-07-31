using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Accounts;

public sealed class SignInWithPasswordUseCase(IUserAccountGateway accounts)
{
    public async Task<PasswordSignInResult> ExecuteAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        string address;
        try
        {
            address = AccountInput.Email(email);
        }
        catch (DomainException)
        {
            await accounts.VerifyDummyPasswordAsync(cancellationToken);
            return PasswordSignInResult.Invalid;
        }

        UserId? userId = await accounts.FindUserIdByEmailAsync(address, cancellationToken);
        if (userId is null)
        {
            await accounts.VerifyDummyPasswordAsync(cancellationToken);
            return PasswordSignInResult.Invalid;
        }

        PasswordCheckResult checkedPassword =
            await accounts.CheckPasswordAsync(userId.Value, password, cancellationToken);
        if (!checkedPassword.HashVerified)
        {
            await accounts.VerifyDummyPasswordAsync(cancellationToken);
        }

        return checkedPassword.Succeeded && !checkedPassword.IsLockedOut
            ? PasswordSignInResult.Success(userId.Value)
            : PasswordSignInResult.Invalid;
    }
}

public sealed record PasswordSignInResult(bool Succeeded, UserId? UserId, string? Code)
{
    public static PasswordSignInResult Invalid { get; } =
        new(false, null, "invalid_credentials");

    public static PasswordSignInResult Success(UserId userId) => new(true, userId, null);
}
