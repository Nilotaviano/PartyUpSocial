using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Friends;

public sealed class AcceptFriendRequestUseCase(
    IFriendPairLocker pairLocker,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    IUnitOfWork unitOfWork,
    IClock clock)
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
            return FriendGraphCommandResult.Failed("friend_request_not_found");
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
                if (action is null)
                {
                    return FriendGraphCommandResult.Failed("friend_request_not_found");
                }

                if (!action.Friendship.IsPending)
                {
                    return FriendGraphCommandResult.Failed("friend_request_not_pending");
                }

                if (action.Friendship.AddresseeId != actorId)
                {
                    return FriendGraphCommandResult.Failed("friend_request_not_found");
                }

                action.Friendship.Accept(actorId, clock);
                FriendGraphCommandSupport.DrainSilentEvents(action.Friendship);
                return FriendGraphCommandResult.Completed(
                    FriendshipResource.From(action.Friendship, actorId, action.Counterpart));
            },
            cancellationToken);
    }
}
