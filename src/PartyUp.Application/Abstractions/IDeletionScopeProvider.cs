using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Abstractions;

public interface IDeletionScopeProvider
{
    Task<DeletionScope> LoadAsync(UserId id, CancellationToken cancellationToken);
}
