using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Abstractions;

public interface IBlockEffectScopeProvider
{
    /// <summary>
    /// Loads and locks everything a block between <paramref name="first"/> and
    /// <paramref name="second"/> can touch (DOMAIN_MODEL §4): both users' sessions and polls, plus
    /// every <see cref="FriendGroup"/> either party owns where the other party
    /// <see cref="FriendGroup.Involves"/> — the D-202 owner↔member membership-deletion scope.
    /// Groups where neither party owns need not be included (member↔member is mask-only).
    /// Implemented in Infrastructure (Task 5).
    /// </summary>
    Task<BlockEffectScope> LoadForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken);
}

public sealed record BlockEffectScope(
    IReadOnlyList<Session> Sessions,
    IReadOnlyList<Poll> Polls,
    IReadOnlyList<FriendGroup> Groups)
{
    public static BlockEffectScope Empty { get; } = new([], [], []);
}
