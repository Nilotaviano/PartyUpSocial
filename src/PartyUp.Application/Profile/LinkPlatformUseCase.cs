using PartyUp.Application.Abstractions;
using PartyUp.Application.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

public sealed class LinkPlatformUseCase(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    ITextPolicy textPolicy)
{
    public Task<PlatformLinkMutationResult> ExecuteAsync(
        LinkPlatformCommand command,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(command.UserId, ct);
                if (user is null)
                {
                    return PlatformLinkMutationResult.Failed("not_found");
                }

                if (command.Platform == Platform.Steam)
                {
                    return PlatformLinkMutationResult.Failed("steam_external_flow_required");
                }

                if (user.PlatformLinks.Any(link => link.Platform == command.Platform))
                {
                    return PlatformLinkMutationResult.Failed("platform_already_linked");
                }

                TextPolicyResult policy = textPolicy.Check(
                    command.Handle,
                    TextField.GamingHandle,
                    user.Locale);
                if (!policy.IsAllowed)
                {
                    return PlatformLinkMutationResult.Failed(policy.ErrorKey!);
                }

                PlatformLink link = user.LinkPlatform(
                    command.Platform,
                    GamingHandle.Create(policy.NormalizedValue));
                return PlatformLinkMutationResult.Completed(link);
            },
            cancellationToken);
}

public sealed record LinkPlatformCommand(UserId UserId, Platform Platform, string Handle);

public sealed record PlatformLinkMutationResult(bool Succeeded, PlatformLink? Link, string? Code)
{
    public static PlatformLinkMutationResult Completed(PlatformLink link) => new(true, link, null);
    public static PlatformLinkMutationResult Failed(string code) => new(false, null, code);
}
