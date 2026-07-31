using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Infrastructure.Deletion;

public sealed class DeletionScopeProvider(IUsernameRegistryRepository usernames)
    : IDeletionScopeProvider
{
    public async Task<DeletionScope> LoadAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        // Phase 2 owns only the username-bearing row in the deletion scope. Later phases extend
        // this loader as their aggregate tables land; the executor contract remains unchanged.
        UsernameRegistryEntry? username = await usernames.FindActiveForOwnerAsync(
            id,
            cancellationToken);
        return new DeletionScope([], [], [], [], username);
    }
}
