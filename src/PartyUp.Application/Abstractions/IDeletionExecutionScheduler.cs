using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

public interface IDeletionExecutionScheduler
{
    ValueTask ScheduleAsync(UserId id, CancellationToken cancellationToken);
}
