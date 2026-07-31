using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Visibility;

// Values may reach read models: append only, never renumber.

/// <summary>Why an identity renders anonymously (DOMAIN_MODEL §7 rendering, in rule order).</summary>
public enum MaskingReason
{
    None = 0,

    /// <summary>D-092: a tombstoned user is Anonymous User for every viewer.</summary>
    Tombstoned = 1,

    /// <summary>D-076/D-086: only between the two members of the blocked pair.</summary>
    Blocked = 2,

    /// <summary>
    /// An <c>Onboarding</c> account has no display name yet (§3). Such a user cannot be invited or
    /// friended (§12-22), so this is a fail-closed fallback rather than a reachable product state.
    /// </summary>
    ProfileIncomplete = 3,
}

/// <summary>
/// How one identity renders to one viewer. The anonymous case carries no text: "Anonymous User" is
/// rendered by the presentation layer in the viewer's locale, exactly like notification bodies
/// (§9), so no name survives here.
/// </summary>
public readonly record struct MaskedIdentity
{
    private MaskedIdentity(MaskingReason reason, DisplayName? displayName, Avatar? avatar)
    {
        Reason = reason;
        DisplayName = displayName;
        Avatar = avatar;
    }

    public MaskingReason Reason { get; }

    public bool IsAnonymous => Reason != MaskingReason.None;

    /// <summary>Null exactly when <see cref="IsAnonymous"/>.</summary>
    public DisplayName? DisplayName { get; }

    /// <summary>Null when anonymous, and for guests, who have no avatar (D-017).</summary>
    public Avatar? Avatar { get; }

    public static MaskedIdentity Anonymous(MaskingReason reason)
    {
        if (reason == MaskingReason.None || !Enum.IsDefined(reason))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"'{reason}' is not a masking reason."));
        }

        return new MaskedIdentity(reason, null, null);
    }

    public static MaskedIdentity Named(DisplayName displayName, Avatar? avatar)
    {
        if (displayName.Value.Length < Kernel.DisplayName.MinLength)
        {
            throw new DomainException(DomainError.ValidationFailed, "A rendered identity needs a display name.");
        }

        return new MaskedIdentity(MaskingReason.None, displayName, avatar);
    }
}
