using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Friendships;

// Domain events carry ids only — never names or rendered text (DOMAIN_MODEL §9).

/// <summary>D-105: the one friend-graph event that produces a notification.</summary>
public sealed record FriendRequestReceived(
    FriendshipId FriendshipId,
    UserId RequestedBy,
    UserId AddresseeId) : IDomainEvent;

/// <summary>D-105 keeps acceptance silent; the event exists for read-model and visibility upkeep.</summary>
public sealed record FriendshipAccepted(
    FriendshipId FriendshipId,
    UserId UserLoId,
    UserId UserHiId) : IDomainEvent;

/// <summary>
/// D-065/D-105: blocking is silent. The cross-aggregate effects of DOMAIN_MODEL §4 are executed by
/// the lifecycle stage, which consumes this.
/// </summary>
public sealed record UserBlocked(BlockId BlockId, UserId BlockerId, UserId BlockedId) : IDomainEvent;
