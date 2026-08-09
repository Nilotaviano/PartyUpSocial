using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

public sealed class GetMyProfileUseCase(
    IUserRepository users,
    ListAuthMethodsUseCase authMethods,
    IPushPromptEligibilityRepository pushPromptEligibility)
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
        bool pushPromptEligible = await pushPromptEligibility.IsEligibleAsync(userId, cancellationToken);
        return new MyProfileResult(user, methods.Methods, pushPromptEligible);
    }
}

public sealed record MyProfileResult(User User, IReadOnlyList<AuthMethod> AuthMethods, bool PushPromptEligible);
