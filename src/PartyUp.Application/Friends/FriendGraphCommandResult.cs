using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Friends;

public sealed record FriendGraphCommandResult(
    bool Succeeded,
    bool Created,
    FriendshipResource? Friendship,
    string? Code)
{
    public static FriendGraphCommandResult CreatedRequest(FriendshipResource friendship) =>
        new(true, true, friendship, null);

    public static FriendGraphCommandResult Completed(FriendshipResource? friendship = null) =>
        new(true, false, friendship, null);

    public static FriendGraphCommandResult Failed(string code) =>
        new(false, false, null, code);
}

public sealed record FriendshipResource(
    FriendshipId FriendshipId,
    string Status,
    string Direction,
    string CounterpartUsername,
    Instant RequestedAt,
    Instant? AcceptedAt)
{
    internal static FriendshipResource From(
        Friendship friendship,
        UserId actorId,
        User counterpart)
    {
        string direction = friendship.IsAccepted
            ? "accepted"
            : friendship.RequestedBy == actorId ? "outgoing" : "incoming";
        return new FriendshipResource(
            friendship.Id,
            friendship.IsAccepted ? "accepted" : "pending",
            direction,
            counterpart.Username!.Value.Value,
            friendship.RequestedAt,
            friendship.AcceptedAt);
    }
}

internal sealed record LockedFriendshipAction(
    Friendship Friendship,
    User Counterpart);

internal static class FriendGraphCommandSupport
{
    internal static async Task<LockedFriendshipAction?> LockActionAsync(
        FriendshipPair authorizedPair,
        FriendshipId friendshipId,
        UserId actorId,
        IFriendPairLocker pairLocker,
        IFriendshipRepository friendships,
        IBlockRepository blocks,
        CancellationToken cancellationToken)
    {
        LockedFriendPair? users = await pairLocker.LockAsync(
            authorizedPair.UserLoId,
            authorizedPair.UserHiId,
            cancellationToken);
        if (users is null)
        {
            return null;
        }

        Friendship? friendship = await friendships.FindByIdForUpdateAsync(
            friendshipId,
            cancellationToken);
        if (friendship is null ||
            !friendship.IsBetween(authorizedPair.UserLoId, authorizedPair.UserHiId) ||
            !friendship.Involves(actorId))
        {
            return null;
        }

        IReadOnlyList<Block> pairBlocks = await blocks.FindPairForUpdateAsync(
            authorizedPair.UserLoId,
            authorizedPair.UserHiId,
            cancellationToken);
        if (pairBlocks.Count != 0 || !users.First.CanBeFriended || !users.Second.CanBeFriended)
        {
            return null;
        }

        User counterpart = users.First.Id == actorId ? users.Second : users.First;
        return new LockedFriendshipAction(friendship, counterpart);
    }

    internal static void DrainSilentEvents(Friendship friendship)
    {
        _ = friendship.DequeueEvents();
    }
}
