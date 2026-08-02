using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Friendships;

/// <summary>
/// One row per ordered (blocker, blocked) pair (DOMAIN_MODEL §4). The row is only the record of the
/// block: its cross-aggregate effects — friendship deletion, participation removal, join-request
/// lapse — are executed by the lifecycle stage, which consumes <see cref="UserBlocked"/>. Visibility
/// is mutual regardless of direction, which is what <see cref="Hides"/> expresses. Unblocking
/// deletes the row and restores nothing (§12-24).
/// </summary>
public sealed class Block : AggregateRoot
{
    private Block()
    {
    }

    private Block(
        BlockId id,
        UserId blockerId,
        UserId blockedId,
        Username blockedUsernameSnapshot,
        Instant createdAt)
    {
        Id = id;
        BlockerId = blockerId;
        BlockedId = blockedId;
        BlockedUsernameSnapshot = blockedUsernameSnapshot;
        CreatedAt = createdAt;
    }

    public BlockId Id { get; private set; }

    public UserId BlockerId { get; private set; }

    public UserId BlockedId { get; private set; }

    public Username BlockedUsernameSnapshot { get; private set; }

    public Instant CreatedAt { get; private set; }

    public static Block Create(
        UserId blockerId,
        UserId blockedId,
        Username blockedUsernameSnapshot,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrEmpty(
            blockedUsernameSnapshot.Value,
            nameof(blockedUsernameSnapshot));

        if (blockerId == blockedId)
        {
            throw new DomainException(DomainError.ValidationFailed, "A user cannot block themselves.");
        }

        Block block = new(
            BlockId.New(),
            blockerId,
            blockedId,
            blockedUsernameSnapshot,
            clock.GetCurrentInstant());
        block.Raise(new UserBlocked(block.Id, blockerId, blockedId));

        return block;
    }

    public bool Involves(UserId userId) => userId == BlockerId || userId == BlockedId;

    /// <summary>D-065: blocking is mutually invisible, so direction does not matter to visibility.</summary>
    public bool Hides(UserId first, UserId second) =>
        first != second && Involves(first) && Involves(second);
}
