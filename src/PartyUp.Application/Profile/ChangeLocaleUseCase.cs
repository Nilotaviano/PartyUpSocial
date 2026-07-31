using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

public sealed class ChangeLocaleUseCase(IUserRepository users, IUnitOfWork unitOfWork)
{
    public Task<ProfileMutationResult> ExecuteAsync(
        UserId userId,
        string locale,
        CancellationToken cancellationToken)
    {
        Locale value = AccountInput.Locale(locale);
        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null)
                {
                    return ProfileMutationResult.Failed("not_found");
                }

                user.ChangeLocale(value);
                return ProfileMutationResult.Completed;
            },
            cancellationToken);
    }
}
