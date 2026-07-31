using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

public interface IUserRepository
{
    Task<User?> FindAsync(UserId id, CancellationToken cancellationToken);

    Task<User?> FindForUpdateAsync(UserId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
        Instant now,
        int max,
        CancellationToken cancellationToken);

    void Add(User user);
}
