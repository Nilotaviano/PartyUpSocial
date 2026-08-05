using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;

namespace PartyUp.Application.Sessions;

/// <summary>
/// A thin composition over S1/S2a's block scope: one <see cref="IUnitOfWork.ExecuteAsync"/> transaction
/// per §5.3, wrapping the existing <see cref="IBlockEffectsExecutor"/> so a Sessions-namespace caller
/// (a future block command endpoint) never has to know the executor's own transaction boundary rules.
/// </summary>
public sealed class SessionBlockEffectsUseCase(
    IBlockEffectsExecutor effectsExecutor,
    IUnitOfWork unitOfWork)
{
    public Task ExecuteAsync(
        Block block,
        IReadOnlyList<Friendship> pairFriendships,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            ct => ExecuteInnerAsync(block, pairFriendships, ct),
            cancellationToken);

    private async Task<bool> ExecuteInnerAsync(
        Block block,
        IReadOnlyList<Friendship> pairFriendships,
        CancellationToken cancellationToken)
    {
        await effectsExecutor.ExecuteAsync(block, pairFriendships, cancellationToken);
        return true;
    }
}
