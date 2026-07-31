using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

public sealed class GetMyProfileUseCase(
    IUserRepository users,
    ListAuthMethodsUseCase authMethods)
{
    public async Task<MyProfileResult?> ExecuteAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        User? user = await users.FindAsync(userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        AuthMethodsResult methods = await authMethods.ExecuteAsync(userId, cancellationToken);
        return new MyProfileResult(user, methods.Methods);
    }
}

public sealed record MyProfileResult(User User, IReadOnlyList<AuthMethod> AuthMethods);
