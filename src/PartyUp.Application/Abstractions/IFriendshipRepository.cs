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

    /// <summary>
    /// A non-locking bulk read of every friendship row between <paramref name="userId"/> and any of
    /// <paramref name="otherUserIds"/> — the set-based replacement for a per-counterpart
    /// <see cref="FindPairAsync"/> loop on a GET path (PHASE4_DESIGN.md §6.1, F6). The default
    /// composes <see cref="FindPairAsync"/> once per counterpart; a widened repository overrides this
    /// with one SQL statement.
    /// </summary>
    Task<IReadOnlyList<Friendship>> FindPairsAmongAsync(
        UserId userId,
        IReadOnlyCollection<UserId> otherUserIds,
        CancellationToken cancellationToken) =>
        CollectPairsAsync(userId, otherUserIds, locking: false, cancellationToken);

    /// <summary>
    /// The A-068 bulk graph lock (§6.1 step 3): locks and returns every friendship row between
    /// <paramref name="userId"/> and any of <paramref name="otherUserIds"/> in one statement, the
    /// table's declared order. The default composes <see cref="FindPairForUpdateAsync"/> once per
    /// counterpart (correct, not single-statement); a widened repository overrides this with the real
    /// bulk-ordered lock.
    /// </summary>
    Task<IReadOnlyList<Friendship>> LockPairsAsync(
        UserId userId,
        IReadOnlyCollection<UserId> otherUserIds,
        CancellationToken cancellationToken) =>
        CollectPairsAsync(userId, otherUserIds, locking: true, cancellationToken);

    private async Task<IReadOnlyList<Friendship>> CollectPairsAsync(
        UserId userId,
        IReadOnlyCollection<UserId> otherUserIds,
        bool locking,
        CancellationToken cancellationToken)
    {
        List<Friendship> results = [];
        foreach (UserId other in otherUserIds)
        {
            Friendship? pair = locking
                ? await FindPairForUpdateAsync(userId, other, cancellationToken)
                : await FindPairAsync(userId, other, cancellationToken);
            if (pair is not null)
            {
                results.Add(pair);
            }
        }

        return results;
    }

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

/// <summary>
/// The counterpart-facing columns a friend list/request row renders — never the full <see cref="User"/>
/// aggregate, so a bulk friend read never materializes each counterpart's <c>AvatarSources</c> or
/// tracks the entity. <see cref="Platforms"/> is empty for a <see cref="PendingFriendProjection"/> row
/// (only <see cref="Username"/> renders there).
/// </summary>
public sealed record FriendCounterpart(
    UserId Id,
    Username? Username,
    DisplayName? DisplayName,
    Avatar? Avatar,
    IReadOnlyList<PlatformLink> Platforms);

public sealed record AcceptedFriendProjection(FriendshipId FriendshipId, FriendCounterpart Counterpart);

public sealed record PendingFriendProjection(
    FriendshipId FriendshipId,
    FriendCounterpart Counterpart,
    UserId RequestedBy,
    Instant RequestedAt);

public sealed class FriendshipPairUniqueConflictException : Exception
{
    public FriendshipPairUniqueConflictException(Exception innerException)
        : base(nameof(FriendshipPairUniqueConflictException), innerException)
    {
    }
}
