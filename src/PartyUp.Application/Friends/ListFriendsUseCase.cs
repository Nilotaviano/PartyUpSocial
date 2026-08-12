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
        FriendCounterpart counterpart = row.Counterpart;
        return new FriendItem(
            row.FriendshipId,
            counterpart.Id,
            counterpart.Username!.Value.Value,
            counterpart.DisplayName!.Value.Value,
            counterpart.Avatar!.Value,
            counterpart.Platforms
                .Select(link => new PublicPlatform(link.Platform, link.Handle.Value))
                .ToArray());
    }
}

public sealed record FriendList(IReadOnlyList<FriendItem> Items);

public sealed record FriendItem(
    FriendshipId FriendshipId,
    UserId UserId,
    string Username,
    string DisplayName,
    Avatar Avatar,
    IReadOnlyList<PublicPlatform> Platforms);
