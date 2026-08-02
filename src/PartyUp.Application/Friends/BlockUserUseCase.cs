using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Friends;

public sealed class BlockUserUseCase(
    IUserGraphResolver resolver,
    IFriendPairLocker pairLocker,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    IBlockEffectsExecutor effectsExecutor,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<BlockGraphCommandResult> ExecuteAsync(
        UserId actorId,
        string? targetUsername,
        CancellationToken cancellationToken)
    {
        string canonical = UsernameRegistry.Canonicalize(targetUsername);
        if (canonical.Length < Username.MinLength || canonical.Length > Username.MaxLength)
        {
            return BlockGraphCommandResult.Failed("validation_failed");
        }

        ResolvedUserGraph? self = await resolver.ResolveSelfAsync(actorId, cancellationToken);
        if (self?.User.Username is { } actorUsername &&
            string.Equals(
                UsernameRegistry.Canonicalize(actorUsername),
                canonical,
                StringComparison.Ordinal))
        {
            return BlockGraphCommandResult.Failed("block_self");
        }

        ResolvedUserGraph? resolved = await resolver.ResolveBlockTargetByUsernameAsync(
            actorId,
            canonical,
            cancellationToken);
        if (resolved is null)
        {
            return BlockGraphCommandResult.Failed("user_not_found");
        }

        UserId targetId = resolved.User.Id;
        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                LockedFriendPair? locked = await pairLocker.LockAsync(actorId, targetId, ct);
                if (locked is null)
                {
                    return BlockGraphCommandResult.Failed("user_not_found");
                }

                User actor = locked.First.Id == actorId ? locked.First : locked.Second;
                User target = locked.First.Id == targetId ? locked.First : locked.Second;
                Friendship? friendship = await friendships.FindPairForUpdateAsync(actorId, targetId, ct);
                IReadOnlyList<Block> pairBlocks = await blocks.FindPairForUpdateAsync(actorId, targetId, ct);

                bool targetIsRelated = friendship is not null;
                if (!actor.CanBeFriended ||
                    !target.IsDiscoverable ||
                    target.IsBanned && !targetIsRelated ||
                    target.Username is null ||
                    !string.Equals(
                        UsernameRegistry.Canonicalize(target.Username.Value),
                        canonical,
                        StringComparison.Ordinal) ||
                    pairBlocks.Count != 0)
                {
                    return BlockGraphCommandResult.Failed("user_not_found");
                }

                Username blockedUsernameSnapshot = target.Username.Value;
                Block block = Block.Create(actorId, targetId, blockedUsernameSnapshot, clock);
                IReadOnlyList<Friendship> pairFriendships = friendship is null ? [] : [friendship];
                await effectsExecutor.ExecuteAsync(block, pairFriendships, ct);
                blocks.Add(block);

                return BlockGraphCommandResult.CreatedBlock(
                    new BlockedUserItem(
                        block.Id,
                        blockedUsernameSnapshot.Value,
                        block.CreatedAt));
            },
            cancellationToken);
    }
}

public sealed record BlockGraphCommandResult(
    bool Succeeded,
    bool Created,
    BlockedUserItem? BlockedUser,
    string? Code)
{
    public static BlockGraphCommandResult CreatedBlock(BlockedUserItem blockedUser) =>
        new(true, true, blockedUser, null);

    public static BlockGraphCommandResult Completed() =>
        new(true, false, null, null);

    public static BlockGraphCommandResult Failed(string code) =>
        new(false, false, null, code);
}
