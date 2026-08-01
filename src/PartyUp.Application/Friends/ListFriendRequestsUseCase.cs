using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Friends;

public sealed class ListFriendRequestsUseCase(IFriendshipRepository friendships)
{
    public async Task<FriendRequestList> ExecuteAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PendingFriendProjection> rows =
            await friendships.ListPendingAsync(userId, cancellationToken);
        return new FriendRequestList(
            rows.Where(row => row.RequestedBy != userId).Select(Map).ToArray(),
            rows.Where(row => row.RequestedBy == userId).Select(Map).ToArray());
    }

    private static FriendRequestItem Map(PendingFriendProjection row) =>
        new(row.FriendshipId, row.Counterpart.Username!.Value.Value, row.RequestedAt);
}

public sealed record FriendRequestList(
    IReadOnlyList<FriendRequestItem> Incoming,
    IReadOnlyList<FriendRequestItem> Outgoing);

public sealed record FriendRequestItem(
    FriendshipId FriendshipId,
    string Username,
    Instant RequestedAt);
