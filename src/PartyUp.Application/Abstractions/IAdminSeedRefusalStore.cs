using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

public interface IAdminSeedRefusalStore
{
    Task<bool> ExistsAsync(UserId userId, CancellationToken cancellationToken);

    Task RecordAsync(
        UserId userId,
        Instant refusedAt,
        CancellationToken cancellationToken);
}
