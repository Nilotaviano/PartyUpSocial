using PartyUp.Domain.Users;

namespace PartyUp.Domain.Visibility;

// Values may reach read models: append only, never renumber.

/// <summary>How much of a profile page one viewer may see (D-068, §12-18).</summary>
public enum ProfileDisclosure
{
    /// <summary>Fail-closed: a blocked counterpart, a tombstone or an Onboarding account.</summary>
    Hidden = 0,

    /// <summary>D-068: before a friendship is accepted, a profile exposes only its unique username.</summary>
    UsernameOnly = 1,

    /// <summary>Self or an Accepted friend: display name, avatar, platform links, the rest.</summary>
    Full = 2,
}

/// <summary>
/// The profile-page half of the §12-18 split. D-068 is an accounts rule and stops here: rosters and
/// voter grids render display name + avatar to anyone with access (D-030/D-114) through
/// <see cref="IdentityMasking"/>, which is a different question with a different answer.
/// </summary>
public static class ProfileVisibility
{
    public static ProfileDisclosure For(User subject, Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(viewer);

        if (viewer.IsBlockedWith(subject.Id))
        {
            return ProfileDisclosure.Hidden;
        }

        if (subject.Id == viewer.Id)
        {
            return ProfileDisclosure.Full;
        }

        if (!LookupVisibility.IsListed(subject))
        {
            return ProfileDisclosure.Hidden;
        }

        return viewer.IsFriendOf(subject.Id) ? ProfileDisclosure.Full : ProfileDisclosure.UsernameOnly;
    }
}
