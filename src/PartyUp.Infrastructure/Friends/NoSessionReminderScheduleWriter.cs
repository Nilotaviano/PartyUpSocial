using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;

namespace PartyUp.Infrastructure.Friends;

public sealed class NoSessionReminderScheduleWriter : IReminderScheduleWriter
{
    public Task ApplyAsync(
        SessionReminderCanceled cancellation,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "The Phase 3 empty block-effect scope cannot produce reminder cancellations.");
}
