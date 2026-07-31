using NodaTime;

namespace PartyUp.Application.Abstractions;

public interface IAuthTokenRetentionPurger
{
    Task<int> PurgeAsync(
        Instant now,
        Duration retention,
        CancellationToken cancellationToken);
}
