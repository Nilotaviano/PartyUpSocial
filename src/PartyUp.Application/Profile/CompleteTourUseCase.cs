using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

/// <summary>
/// D-182: finishing or skipping the first-login tour both call this. Idempotent — the domain's
/// <see cref="User.CompleteTour"/> keeps the first instant, so a retried or replayed call never
/// moves it.
/// </summary>
public sealed class CompleteTourUseCase(IUserRepository users, IUnitOfWork unitOfWork, IClock clock)
{
    public Task<ProfileMutationResult> ExecuteAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null)
                {
                    return ProfileMutationResult.Failed("not_found");
                }

                user.CompleteTour(clock);
                return ProfileMutationResult.Completed;
            },
            cancellationToken);
}
