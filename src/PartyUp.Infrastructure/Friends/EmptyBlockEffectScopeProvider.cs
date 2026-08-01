using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Friends;

public sealed class EmptyBlockEffectScopeProvider : IBlockEffectScopeProvider
{
    public Task<BlockEffectScope> LoadForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken) =>
        Task.FromResult(BlockEffectScope.Empty);
}
