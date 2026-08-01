using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Abstractions;

public interface IBlockEffectScopeProvider
{
    Task<BlockEffectScope> LoadForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken);
}

public sealed record BlockEffectScope(
    IReadOnlyList<Session> Sessions,
    IReadOnlyList<Poll> Polls)
{
    public static BlockEffectScope Empty { get; } = new([], []);
}
