namespace PartyUp.Domain.Kernel;

// Values in this file are persisted: append only, never renumber.

public enum SessionStatus
{
    Unconfirmed = 0,
    Confirmed = 1,
    Completed = 2,
    Expired = 3,
    Canceled = 4,
}

public enum ParticipationStatus
{
    Pending = 0,
    Going = 1,
    Maybe = 2,
    Declined = 3,
    Removed = 4,
}

public enum ParticipationOrigin
{
    Invited = 0,
    JoinRequest = 1,
    GuestLink = 2,
    PollFinalization = 3,
}

public enum JoinRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Withdrawn = 3,
    Lapsed = 4,
}

public enum GuestLinkState
{
    Active = 0,
    RevokedByCancel = 1,
    RevokedByRemoval = 2,
    ExpiredPoll = 3,

    /// <summary>D-225: the seat now belongs to a registered account; the bearer link is spent.</summary>
    Upgraded = 4,
}
