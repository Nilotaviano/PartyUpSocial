using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Friends;

public sealed class UnfriendUseCase(
    IFriendPairLocker pairLocker,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    IUnitOfWork unitOfWork)
{
    public async Task<FriendGraphCommandResult> ExecuteAsync(
        UserId actorId,
        FriendshipId friendshipId,
        CancellationToken cancellationToken)
    {
        FriendshipPair? authorizedPair = await friendships.FindOwnedPairAsync(
            friendshipId,
            actorId,
            cancellationToken);
        if (authorizedPair is null)
        {
            return FriendGraphCommandResult.Failed("friendship_not_found");
        }

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                LockedFriendshipAction? action = await FriendGraphCommandSupport.LockActionAsync(
                    authorizedPair,
                    friendshipId,
                    actorId,
                    pairLocker,
                    friendships,
                    blocks,
                    ct);
                if (action is null || !action.Friendship.IsAccepted)
                {
                    return FriendGraphCommandResult.Failed("friendship_not_found");
                }

                friendships.Remove(action.Friendship);
                return FriendGraphCommandResult.Completed();
            },
            cancellationToken);
    }
}
