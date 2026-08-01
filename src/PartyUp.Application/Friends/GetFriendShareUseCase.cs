using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Friends;

public sealed class GetFriendShareUseCase(
    IUserGraphResolver resolver,
    IPublicLinkBuilder links)
{
    public async Task<FriendShare?> ExecuteAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        User? user = (await resolver.ResolveSelfAsync(userId, cancellationToken))?.User;
        if (user?.Username is not { } username || user.FriendCode is not { } friendCode)
        {
            return null;
        }

        return new FriendShare(
            friendCode.Value,
            links.Add(friendCode),
            links.Profile(username));
    }
}

public sealed record FriendShare(string FriendCode, string AddUrl, string ProfileUrl);
