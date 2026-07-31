using PartyUp.Domain.Users;

namespace PartyUp.Domain.Visibility;

/// <summary>
/// Who may surface on a lookup surface — profile link, friend code, QR resolution, username search,
/// the friend-request command and Steam friend-list import matching (DOMAIN_MODEL §4). Two classes
/// of user are excluded from all of them: an <c>Onboarding</c> account, whose half-built profile
/// must not surface (§12-22, D-127), and a tombstone, whose friend code and username are retired
/// (§3, D-092). A blocked counterpart is never surfaced either (D-049/D-065).
/// </summary>
public static class LookupVisibility
{
    /// <summary>
    /// §3/§12-22, viewer-independent. <c>PendingDeletion</c> stays listed: the grace period has no
    /// domain effects (§12-34).
    /// </summary>
    public static bool IsListed(User candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return candidate.IsDiscoverable;
    }

    public static bool CanSurface(User candidate, Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(viewer);

        return IsListed(candidate) && !viewer.IsBlockedWith(candidate.Id);
    }

    /// <summary>
    /// The friend-request command (§4): a tombstone can neither send nor receive requests, an
    /// Onboarding user cannot be friended (§12-22), a blocked counterpart is invisible, and nobody
    /// friends themselves.
    /// </summary>
    public static bool CanReceiveFriendRequest(User candidate, Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(viewer);

        return candidate.Id != viewer.Id && CanSurface(candidate, viewer);
    }

    /// <summary>
    /// D-049/D-065: Steam friend-list import matching is a lookup surface like any other — a blocked
    /// counterpart or a tombstoned user is never offered as a match.
    /// </summary>
    public static bool CanMatchSteamImport(User candidate, Viewer importer) =>
        CanReceiveFriendRequest(candidate, importer);
}
