namespace PartyUp.Domain.Kernel;

// Values are persisted and carried across layer boundaries: append only, never renumber.
public enum DomainError
{
    ValidationFailed = 0,
    NotFound = 1,
    Forbidden = 2,
    Conflict = 3,
    Duplicate = 4,
    CapacityExceeded = 5,
    WindowClosed = 6,
    TerminalState = 7,
    InvalidTransition = 8,
    Blocked = 9,
    LimitExceeded = 10,
    RemovalIsPermanent = 11,
    RangeTooLarge = 12,
    CooldownActive = 13,
    NameReserved = 14,
}
