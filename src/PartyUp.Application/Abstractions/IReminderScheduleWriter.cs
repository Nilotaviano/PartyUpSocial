using PartyUp.Domain.Friendships;

namespace PartyUp.Application.Abstractions;

public interface IReminderScheduleWriter
{
    Task ApplyAsync(
        SessionReminderCanceled cancellation,
        CancellationToken cancellationToken);
}
