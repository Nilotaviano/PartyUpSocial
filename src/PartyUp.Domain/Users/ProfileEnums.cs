namespace PartyUp.Domain.Users;

// Values in this file are persisted: append only, never renumber.

/// <summary>Which source an avatar draws from (DOMAIN_MODEL §3, D-050).</summary>
public enum AvatarKind
{
    Emoji = 0,
    Platform = 1,
}

/// <summary>Where the age signal came from (D-093); no birth date is ever retained.</summary>
public enum AgeGateSource
{
    SelfAttested = 0,
    OperatingSystem = 1,
    Store = 2,
}

/// <summary>State of a row in the one username constraint domain (DOMAIN_MODEL §3).</summary>
public enum UsernameRegistryState
{
    Active = 0,
    Reserved = 1,
}
