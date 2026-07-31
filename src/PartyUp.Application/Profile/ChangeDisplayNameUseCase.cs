using PartyUp.Application.Abstractions;
using PartyUp.Application.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

public sealed class ChangeDisplayNameUseCase(
    IUserRepository users,
    ITextPolicy textPolicy,
    IUnitOfWork unitOfWork)
{
    public Task<ProfileMutationResult> ExecuteAsync(
        UserId userId,
        string candidate,
        CancellationToken cancellationToken)
    {
        TextPolicyResult policy = textPolicy.Check(candidate, TextField.DisplayName, locale: null);
        if (!policy.IsAllowed)
        {
            return Task.FromResult(ProfileMutationResult.Failed(policy.ErrorKey!));
        }

        DisplayName displayName = DisplayName.Create(policy.NormalizedValue);
        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null)
                {
                    return ProfileMutationResult.Failed("not_found");
                }

                user.ChangeDisplayName(displayName);
                return ProfileMutationResult.Completed;
            },
            cancellationToken);
    }
}
