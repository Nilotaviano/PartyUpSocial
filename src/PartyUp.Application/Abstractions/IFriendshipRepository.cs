using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

public interface IFriendshipRepository
{
    Task<Friendship?> FindPairAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken);

    Task<Friendship?> FindPairForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken);

    Task<FriendshipPair?> FindOwnedPairAsync(
        FriendshipId friendshipId,
        UserId actorId,
        CancellationToken cancellationToken);

    Task<Friendship?> FindByIdForUpdateAsync(
        FriendshipId friendshipId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AcceptedFriendProjection>> ListAcceptedAsync(
        UserId userId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingFriendProjection>> ListPendingAsync(
        UserId userId,
        CancellationToken cancellationToken);

    void Add(Friendship friendship);

    void Remove(Friendship friendship);
}

public sealed record FriendshipPair(UserId UserLoId, UserId UserHiId);

public sealed record AcceptedFriendProjection(FriendshipId FriendshipId, User Counterpart);

public sealed record PendingFriendProjection(
    FriendshipId FriendshipId,
    User Counterpart,
    UserId RequestedBy,
    Instant RequestedAt);
