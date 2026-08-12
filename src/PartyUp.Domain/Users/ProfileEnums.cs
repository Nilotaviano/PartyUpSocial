namespace PartyUp.Domain.Users;

// Values in this file are persisted: append only, never renumber.

/// <summary>Which source an avatar draws from (DOMAIN_MODEL §3, D-050/D-190).</summary>
public enum AvatarKind
{
    Emoji = 0,

    /// <summary>Draws its picture from one of the user's <see cref="AvatarSource"/> rows.</summary>
    External = 1,
}

/// <summary>The linked account an <see cref="AvatarSource"/> draws its picture from (AVATARS_DESIGN.md §3.1, D-190).</summary>
public enum AvatarSourceKind
{
    SteamPlatform = 0,
    GoogleAccount = 1,
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
