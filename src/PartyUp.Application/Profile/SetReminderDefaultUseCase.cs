using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

public sealed class SetReminderDefaultUseCase(IUserRepository users, IUnitOfWork unitOfWork)
{
    public Task<ProfileMutationResult> ExecuteAsync(
        UserId userId,
        ReminderOffset reminder,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null)
                {
                    return ProfileMutationResult.Failed("not_found");
                }

                user.SetReminderDefault(reminder);
                return ProfileMutationResult.Completed;
            },
            cancellationToken);
}
