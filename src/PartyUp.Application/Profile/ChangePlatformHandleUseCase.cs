using PartyUp.Application.Abstractions;
using PartyUp.Application.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

public sealed class ChangePlatformHandleUseCase(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    ITextPolicy textPolicy)
{
    public Task<PlatformLinkMutationResult> ExecuteAsync(
        ChangePlatformHandleCommand command,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(command.UserId, ct);
                if (user is null)
                {
                    return PlatformLinkMutationResult.Failed("not_found");
                }

                PlatformLink? link = user.PlatformLinks.SingleOrDefault(
                    value => value.Id == command.PlatformLinkId);
                if (link is null)
                {
                    return PlatformLinkMutationResult.Failed("not_found");
                }

                TextPolicyResult policy = textPolicy.Check(
                    command.Handle,
                    TextField.GamingHandle,
                    user.Locale);
                if (!policy.IsAllowed)
                {
                    return PlatformLinkMutationResult.Failed(policy.ErrorKey!);
                }

                user.ChangePlatformHandle(
                    command.PlatformLinkId,
                    GamingHandle.Create(policy.NormalizedValue));
                return PlatformLinkMutationResult.Completed(link);
            },
            cancellationToken);
}

public sealed record ChangePlatformHandleCommand(
    UserId UserId,
    PlatformLinkId PlatformLinkId,
    string Handle);
