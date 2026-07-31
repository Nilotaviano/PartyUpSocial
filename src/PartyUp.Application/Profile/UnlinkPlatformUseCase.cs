using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

public sealed class UnlinkPlatformUseCase(IUserRepository users, IUnitOfWork unitOfWork)
{
    public Task<UnlinkPlatformResult> ExecuteAsync(
        UserId userId,
        PlatformLinkId platformLinkId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                PlatformLink? link = user?.PlatformLinks.SingleOrDefault(value => value.Id == platformLinkId);
                if (link is null)
                {
                    return UnlinkPlatformResult.NotFound;
                }

                if (link.Platform == Platform.Steam)
                {
                    return UnlinkPlatformResult.Steam;
                }

                user!.UnlinkPlatform(platformLinkId);
                return UnlinkPlatformResult.Removed;
            },
            cancellationToken);
}

public enum UnlinkPlatformResult
{
    Removed,
    Steam,
    NotFound,
}
