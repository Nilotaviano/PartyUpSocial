using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence;

public enum SessionReminderHandoffState
{
    Pending = 0,
    Canceled = 1,
    Delivered = 2,
    Failed = 3,
}

public sealed class SessionReminderHandoff
{
    public Guid Id { get; set; }
    public SessionId SessionId { get; set; }
    public UserId RecipientUserId { get; set; }
    public Instant DueAt { get; set; }
    public SessionReminderHandoffState State { get; set; }
    public Instant PlannedAt { get; set; }
    public Instant? DeliveredAt { get; set; }
    public int Version { get; set; }
    public int Attempts { get; set; }
    public Instant? LastAttemptAt { get; set; }
}
