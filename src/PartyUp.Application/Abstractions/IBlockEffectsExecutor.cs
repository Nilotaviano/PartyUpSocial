using PartyUp.Domain.Friendships;

namespace PartyUp.Application.Abstractions;

public interface IBlockEffectsExecutor
{
    Task ExecuteAsync(
        Block block,
        IReadOnlyList<Friendship> pairFriendships,
        CancellationToken cancellationToken);
}
