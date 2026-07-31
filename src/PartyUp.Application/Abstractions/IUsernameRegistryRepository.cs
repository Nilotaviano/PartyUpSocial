using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

public interface IUsernameRegistryRepository
{
    Task<UsernameRegistryEntry?> FindAsync(string canonical, CancellationToken cancellationToken);

    Task<UsernameRegistryEntry?> FindActiveForOwnerAsync(
        UserId owner,
        CancellationToken cancellationToken);

    void Add(UsernameRegistryEntry entry);
}
