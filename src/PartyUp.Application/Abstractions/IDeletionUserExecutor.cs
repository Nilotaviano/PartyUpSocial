using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

public interface IDeletionUserExecutor
{
    Task<bool> ExecuteAsync(UserId id, CancellationToken cancellationToken);
}
