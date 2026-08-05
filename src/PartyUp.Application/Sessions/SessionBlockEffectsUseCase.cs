using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Sessions;

/// <summary>
/// A thin composition over S1/S2a's block scope: one <see cref="IUnitOfWork.ExecuteAsync"/> transaction
/// per §5.3, wrapping the existing <see cref="IBlockEffectsExecutor"/> so a Sessions-namespace caller
/// (a future block command endpoint) never has to know the executor's own transaction boundary rules.
/// Only the <see cref="BlockId"/> crosses the transaction boundary (A-069): the block and its pair
/// friendship are loaded fresh inside the delegate on every attempt, including a retried one, rather
/// than accepted pre-loaded from outside.
/// </summary>
public sealed class SessionBlockEffectsUseCase(
    IBlockRepository blocks,
    IFriendshipRepository friendships,
    IBlockEffectsExecutor effectsExecutor,
    IUnitOfWork unitOfWork)
{
    public Task<bool> ExecuteAsync(BlockId blockId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            ct => ExecuteInnerAsync(blockId, ct),
            cancellationToken);

    private async Task<bool> ExecuteInnerAsync(BlockId blockId, CancellationToken cancellationToken)
    {
        Block? block = await blocks.FindByIdForUpdateAsync(blockId, cancellationToken);
        if (block is null)
        {
            return false;
        }

        Friendship? pairFriendship = await friendships.FindPairForUpdateAsync(
            block.BlockerId, block.BlockedId, cancellationToken);
        IReadOnlyList<Friendship> pairFriendships = pairFriendship is null ? [] : [pairFriendship];

        await effectsExecutor.ExecuteAsync(block, pairFriendships, cancellationToken);
        return true;
    }
}
