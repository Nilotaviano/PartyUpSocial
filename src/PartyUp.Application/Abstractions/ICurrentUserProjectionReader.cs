using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

public interface ICurrentUserProjectionReader
{
    Task<CurrentUserProjection?> ReadAsync(UserId id, CancellationToken cancellationToken);
}
