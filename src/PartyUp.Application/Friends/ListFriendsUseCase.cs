using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Friends;

public sealed class ListFriendsUseCase(IFriendshipRepository friendships)
{
    public async Task<FriendList> ExecuteAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AcceptedFriendProjection> rows =
            await friendships.ListAcceptedAsync(userId, cancellationToken);
        return new FriendList(rows.Select(Map).ToArray());
    }

    private static FriendItem Map(AcceptedFriendProjection row)
    {
        User user = row.Counterpart;
        return new FriendItem(
            row.FriendshipId,
            user.Username!.Value.Value,
            user.DisplayName!.Value.Value,
            user.Avatar!.Value,
            user.PlatformLinks
                .Select(link => new PublicPlatform(link.Platform, link.Handle.Value))
                .ToArray());
    }
}

public sealed record FriendList(IReadOnlyList<FriendItem> Items);

public sealed record FriendItem(
    FriendshipId FriendshipId,
    string Username,
    string DisplayName,
    Avatar Avatar,
    IReadOnlyList<PublicPlatform> Platforms);
