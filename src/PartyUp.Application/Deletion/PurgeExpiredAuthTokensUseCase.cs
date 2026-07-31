using NodaTime;
using PartyUp.Application.Abstractions;

namespace PartyUp.Application.Deletion;

public sealed class PurgeExpiredAuthTokensUseCase(
    IAuthTokenRetentionPurger purger,
    IUnitOfWork unitOfWork,
    AuthTokenRetentionOptions options,
    IClock clock)
{
    public Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (options.Retention <= Duration.Zero)
        {
            return Task.FromResult(0);
        }

        return unitOfWork.ExecuteAsync(
            ct => purger.PurgeAsync(clock.GetCurrentInstant(), options.Retention, ct),
            cancellationToken);
    }
}

public sealed record AuthTokenRetentionOptions(Duration Retention);
