using PartyUp.Application.Abstractions;
using PartyUp.Application.Onboarding;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

public sealed class ChangeAvatarUseCase(IUserRepository users, IUnitOfWork unitOfWork)
{
    public Task<ProfileMutationResult> ExecuteAsync(
        UserId userId,
        AvatarDraft draft,
        CancellationToken cancellationToken)
    {
        Avatar avatar;
        try
        {
            avatar = ValidateProfileDraftUseCase.ToAvatar(draft);
        }
        catch (DomainException)
        {
            return Task.FromResult(ProfileMutationResult.Failed("errors.avatar.invalid"));
        }

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null)
                {
                    return ProfileMutationResult.Failed("not_found");
                }

                try
                {
                    user.ChangeAvatar(avatar);
                }
                catch (DomainException)
                {
                    // A source draft that names another user's avatar source (or one that no
                    // longer exists) fails User.ChangeAvatar's D-050 ownership check, not the
                    // pure ToAvatar catalog check above -- same client-facing code either way.
                    return ProfileMutationResult.Failed("errors.avatar.invalid");
                }

                return ProfileMutationResult.Completed;
            },
            cancellationToken);
    }
}
