using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Visibility;

/// <summary>
/// A registered viewer plus the two relation sets every §7 rule reads: their Accepted friendships
/// and their block pairs in either direction. The application layer feeds these from query results;
/// the engine itself touches no repository.
/// </summary>
public sealed class Viewer
{
    private readonly HashSet<UserId> _acceptedFriendIds;
    private readonly HashSet<UserId> _blockedCounterpartIds;

    private Viewer(UserId id, HashSet<UserId> acceptedFriendIds, HashSet<UserId> blockedCounterpartIds)
    {
        Id = id;
        _acceptedFriendIds = acceptedFriendIds;
        _blockedCounterpartIds = blockedCounterpartIds;
    }

    public UserId Id { get; }

    public IReadOnlyCollection<UserId> AcceptedFriendIds => _acceptedFriendIds;

    /// <summary>Both directions: blocking is mutually invisible, so who blocked whom never matters (D-065).</summary>
    public IReadOnlyCollection<UserId> BlockedCounterpartIds => _blockedCounterpartIds;

    public static Viewer Create(
        UserId id,
        IEnumerable<UserId>? acceptedFriendIds = null,
        IEnumerable<UserId>? blockedCounterpartIds = null)
    {
        HashSet<UserId> friends = Collect(id, acceptedFriendIds, "a friend of");
        HashSet<UserId> blocked = Collect(id, blockedCounterpartIds, "blocked with");

        return new Viewer(id, friends, blocked);
    }

    /// <summary>
    /// Builds the two sets from the rows the application already loaded. Pending friendships are
    /// ignored — only an Accepted one extends visibility (§7 rule 2) — and rows that do not involve
    /// the viewer are skipped rather than trusted.
    /// </summary>
    public static Viewer From(UserId id, IEnumerable<Friendship> friendships, IEnumerable<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(friendships);
        ArgumentNullException.ThrowIfNull(blocks);

        HashSet<UserId> friends = [];
        foreach (Friendship friendship in friendships)
        {
            if (friendship.IsAccepted && friendship.Involves(id))
            {
                friends.Add(friendship.Counterpart(id));
            }
        }

        HashSet<UserId> blocked = [];
        foreach (Block block in blocks)
        {
            if (block.Involves(id))
            {
                blocked.Add(block.BlockerId == id ? block.BlockedId : block.BlockerId);
            }
        }

        return new Viewer(id, friends, blocked);
    }

    public bool IsFriendOf(UserId other) => _acceptedFriendIds.Contains(other);

    public bool IsBlockedWith(UserId other) => _blockedCounterpartIds.Contains(other);

    private static HashSet<UserId> Collect(UserId id, IEnumerable<UserId>? candidates, string relation)
    {
        HashSet<UserId> set = [];

        if (candidates is null)
        {
            return set;
        }

        foreach (UserId candidate in candidates)
        {
            if (candidate == id)
            {
                throw new DomainException(
                    DomainError.ValidationFailed,
                    FormattableString.Invariant($"A viewer is never {relation} themselves."));
            }

            set.Add(candidate);
        }

        return set;
    }
}
