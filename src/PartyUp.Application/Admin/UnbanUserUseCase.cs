using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Admin;

public sealed class UnbanUserUseCase(
    IUserRepository users,
    IUnitOfWork unitOfWork)
{
    public Task<AdminUserMutationResult> ExecuteAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null)
                {
                    return AdminUserMutationResult.Failed("not_found");
                }

                user.Unban();
                return AdminUserMutationResult.Completed;
            },
            cancellationToken);
}
