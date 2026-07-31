using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Deletion;

public sealed class ExecuteDueDeletionsUseCase(
    IUserRepository users,
    IDeletionUserExecutor executor,
    IClock clock)
{
    public async Task<DeletionSweepResult> ExecuteAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<UserId> due = await users.FindDueForDeletionAsync(
            clock.GetCurrentInstant(),
            batchSize,
            cancellationToken);
        int executed = 0;
        int failed = 0;
        foreach (UserId id in due)
        {
            try
            {
                if (await executor.ExecuteAsync(id, cancellationToken))
                {
                    executed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                failed++;
            }
        }

        return new DeletionSweepResult(executed, failed);
    }
}

public sealed record DeletionSweepResult(int Executed, int Failed);
