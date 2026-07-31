using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

public interface IAccountSessionService
{
    Task SignOutEverywhereAsync(UserId id, CancellationToken cancellationToken);
}
