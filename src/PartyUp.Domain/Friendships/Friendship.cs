using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Friendships;

/// <summary>
/// One row per unordered pair, stored canonically as <c>UserLoId &lt; UserHiId</c> so the DB unique
/// index catches both directions of the same friendship (DOMAIN_MODEL §4). Rejection and removal
/// (D-105) delete the row — there is no stored terminal state and no domain cooldown on
/// re-requesting (§12-23).
/// </summary>
public sealed class Friendship : AggregateRoot
{
    private Friendship()
    {
    }

    private Friendship(FriendshipId id, UserId userLoId, UserId userHiId, UserId requestedBy, Instant requestedAt)
    {
        Id = id;
        UserLoId = userLoId;
        UserHiId = userHiId;
        RequestedBy = requestedBy;
        Status = FriendshipStatus.Pending;
        RequestedAt = requestedAt;
    }

    public FriendshipId Id { get; private set; }

    public UserId UserLoId { get; private set; }

    public UserId UserHiId { get; private set; }

    public UserId RequestedBy { get; private set; }

    public FriendshipStatus Status { get; private set; }

    public Instant RequestedAt { get; private set; }

    public Instant? AcceptedAt { get; private set; }

    public UserId AddresseeId => RequestedBy == UserLoId ? UserHiId : UserLoId;

    public bool IsPending => Status == FriendshipStatus.Pending;

    public bool IsAccepted => Status == FriendshipStatus.Accepted;

    /// <summary>The canonical (lo, hi) ordering every friendship row is stored in.</summary>
    public static (UserId Lo, UserId Hi) CanonicalPair(UserId first, UserId second)
    {
        if (first == second)
        {
            throw new DomainException(DomainError.ValidationFailed, "A friendship needs two different users.");
        }

        return first.Value.CompareTo(second.Value) < 0 ? (first, second) : (second, first);
    }

    /// <summary>
    /// D-015: a request is one half of a mutual connection. Both parties must be discoverable —
    /// an <c>Onboarding</c> user cannot be friended (§12-22) and neither can a tombstone (§3).
    /// Block filtering is a cross-aggregate concern applied by the lifecycle stage above this.
    /// </summary>
    public static Friendship Request(User requester, User addressee, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(addressee);
        ArgumentNullException.ThrowIfNull(clock);

        (UserId lo, UserId hi) = CanonicalPair(requester.Id, addressee.Id);

        if (!requester.CanBeFriended || !addressee.CanBeFriended)
        {
            throw new DomainException(
                DomainError.Forbidden,
                "Only discoverable users can send or receive friend requests.");
        }

        Friendship friendship = new(FriendshipId.New(), lo, hi, requester.Id, clock.GetCurrentInstant());
        friendship.Raise(new FriendRequestReceived(friendship.Id, requester.Id, addressee.Id));

        return friendship;
    }

    /// <summary>D-015: only the other party can accept, and only once.</summary>
    public void Accept(UserId acceptor, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!IsPending)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                "This friendship has already been accepted.");
        }

        if (!Involves(acceptor))
        {
            throw new DomainException(DomainError.Forbidden, "Only a member of the pair can accept this request.");
        }

        if (acceptor == RequestedBy)
        {
            throw new DomainException(
                DomainError.Forbidden,
                "A friend request is accepted by the other person, not by its requester.");
        }

        Status = FriendshipStatus.Accepted;
        AcceptedAt = clock.GetCurrentInstant();

        Raise(new FriendshipAccepted(Id, UserLoId, UserHiId));
    }

    public bool Involves(UserId userId) => userId == UserLoId || userId == UserHiId;

    public bool IsBetween(UserId first, UserId second) =>
        first != second && Involves(first) && Involves(second);

    public UserId Counterpart(UserId userId) => userId == UserLoId
        ? UserHiId
        : userId == UserHiId
            ? UserLoId
            : throw new DomainException(DomainError.NotFound, "This user is not part of the friendship.");
}
