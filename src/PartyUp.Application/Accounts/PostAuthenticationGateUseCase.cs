using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Accounts;

public sealed class PostAuthenticationGateUseCase(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<PostAuthenticationResult> ExecuteAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync<PostAuthenticationResult>(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);

                if (user is null || user.IsTombstoned)
                {
                    return new PostAuthenticationResult.Refused("invalid_credentials");
                }

                if (user.IsBanned)
                {
                    return new PostAuthenticationResult.Refused("account_banned");
                }

                if (!user.IsPendingDeletion)
                {
                    return new PostAuthenticationResult.Admitted(user.Id, false);
                }

                if (user.IsGraceElapsed(clock))
                {
                    return new PostAuthenticationResult.Refused("account_pending_removal");
                }

                user.CancelDeletion(clock);
                return new PostAuthenticationResult.Admitted(user.Id, true);
            },
            cancellationToken);
}
