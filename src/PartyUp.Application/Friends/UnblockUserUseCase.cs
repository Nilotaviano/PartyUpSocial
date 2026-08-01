using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Friends;

public sealed class UnblockUserUseCase(
    IFriendPairLocker pairLocker,
    IBlockRepository blocks,
    IUnitOfWork unitOfWork)
{
    public async Task<BlockGraphCommandResult> ExecuteAsync(
        UserId actorId,
        BlockId blockId,
        CancellationToken cancellationToken)
    {
        BlockPair? authorizedPair = await blocks.FindOutboundOwnedPairAsync(
            blockId,
            actorId,
            cancellationToken);
        if (authorizedPair is null)
        {
            return BlockGraphCommandResult.Failed("block_not_found");
        }

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                LockedFriendPair? users = await pairLocker.LockAsync(
                    authorizedPair.BlockerId,
                    authorizedPair.BlockedId,
                    ct);
                if (users is null)
                {
                    return BlockGraphCommandResult.Failed("block_not_found");
                }

                Block? block = await blocks.FindByIdForUpdateAsync(blockId, ct);
                if (block is null ||
                    block.BlockerId != actorId ||
                    block.BlockerId != authorizedPair.BlockerId ||
                    block.BlockedId != authorizedPair.BlockedId)
                {
                    return BlockGraphCommandResult.Failed("block_not_found");
                }

                _ = BlockEffects.Unblock(block);
                blocks.Remove(block);
                return BlockGraphCommandResult.Completed();
            },
            cancellationToken);
    }
}
