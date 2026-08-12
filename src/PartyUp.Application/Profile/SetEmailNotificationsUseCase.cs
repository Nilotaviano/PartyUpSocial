using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

/// <summary>D-189: the settings-page toggle, in the <see cref="ChangeLocaleUseCase"/> idiom.</summary>
public sealed class SetEmailNotificationsUseCase(IUserRepository users, IUnitOfWork unitOfWork)
{
    public Task<ProfileMutationResult> ExecuteAsync(
        UserId userId,
        bool enabled,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null)
                {
                    return ProfileMutationResult.Failed("not_found");
                }

                user.SetEmailNotifications(enabled);
                return ProfileMutationResult.Completed;
            },
            cancellationToken);
}
