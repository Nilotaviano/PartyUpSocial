using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Admin;

public sealed class BanUserUseCase(
    IUserRepository users,
    IAccountSessionService sessions,
    IUnitOfWork unitOfWork,
    IClock clock)
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

                user.Ban(clock);
                await sessions.SignOutEverywhereAsync(userId, ct);
                return AdminUserMutationResult.Completed;
            },
            cancellationToken);
}

public sealed record AdminUserMutationResult(bool Succeeded, string? Code)
{
    public static AdminUserMutationResult Completed { get; } = new(true, null);

    public static AdminUserMutationResult Failed(string code) => new(false, code);
}
