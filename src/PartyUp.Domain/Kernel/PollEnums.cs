using NodaTime;

namespace PartyUp.Domain.Kernel;

// Values in this file are persisted: append only, never renumber.

public enum PollStatus
{
    Polling = 0,
    Closed = 1,
    Finalized = 2,
    Expired = 3,
    Canceled = 4,
}

public enum VoteChoice
{
    Available = 0,
    Maybe = 1,
    Unavailable = 2,
}

public enum PollParticipantStatus
{
    Active = 0,
    Removed = 1,
}

/// <summary>Voting deadline offsets from poll creation (D-070); the default is 24 hours (D-131).</summary>
public enum PollDeadlineChoice
{
    Hours6 = 0,
    Hours12 = 1,
    Hours24 = 2,
    Hours48 = 3,
}

public static class PollDeadlineChoiceExtensions
{
    public static Duration ToDuration(this PollDeadlineChoice choice) => choice switch
    {
        PollDeadlineChoice.Hours6 => Duration.FromHours(6),
        PollDeadlineChoice.Hours12 => Duration.FromHours(12),
        PollDeadlineChoice.Hours24 => Duration.FromHours(24),
        PollDeadlineChoice.Hours48 => Duration.FromHours(48),
        _ => throw new DomainException(
            DomainError.ValidationFailed,
            FormattableString.Invariant($"Unknown poll deadline choice '{choice}'.")),
    };

    public static Instant DeadlineFrom(this PollDeadlineChoice choice, Instant createdAt) =>
        createdAt + choice.ToDuration();
}
