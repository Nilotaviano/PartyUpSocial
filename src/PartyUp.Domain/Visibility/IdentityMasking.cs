using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Visibility;

/// <summary>
/// The one rendering service of DOMAIN_MODEL §7, applied equally to rosters, poll voter grids and
/// option attribution. Its rules run in order: tombstone wins over everything (D-092), then the
/// blocked pair masks each other and nobody else (D-076/D-086), then everyone with access sees the
/// display name and avatar (D-030/D-114) — D-068's username-only rule governs profile pages, not
/// rosters (§12-18, <see cref="ProfileVisibility"/>).
/// </summary>
/// <remarks>
/// Masking is presentation on top of real access: it never changes attendance, capacity or
/// confirmation, which is what makes a blocked pair's third-party session work (D-086).
/// </remarks>
public static class IdentityMasking
{
    public static MaskedIdentity ForUser(User subject, Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(viewer);

        if (subject.IsTombstoned)
        {
            return MaskedIdentity.Anonymous(MaskingReason.Tombstoned);
        }

        if (viewer.IsBlockedWith(subject.Id))
        {
            return MaskedIdentity.Anonymous(MaskingReason.Blocked);
        }

        return Named(subject);
    }

    /// <summary>
    /// What a guest link's bearer sees (§5.4): the same rendering as a participant's view
    /// (D-030/D-114), minus the block rule — guests cannot block or be blocked in the MVP (D-150).
    /// </summary>
    public static MaskedIdentity ForUserSeenByGuest(User subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        return subject.IsTombstoned
            ? MaskedIdentity.Anonymous(MaskingReason.Tombstoned)
            : Named(subject);
    }

    /// <summary>Guest entries render their self-supplied display name as-is (§7, D-017/D-150).</summary>
    public static MaskedIdentity ForGuestIdentity(GuestIdentity guest)
    {
        ArgumentNullException.ThrowIfNull(guest);

        return MaskedIdentity.Named(guest.DisplayName, null);
    }

    private static MaskedIdentity Named(User subject) =>
        subject.DisplayName is { } displayName
            ? MaskedIdentity.Named(displayName, subject.Avatar)
            : MaskedIdentity.Anonymous(MaskingReason.ProfileIncomplete);
}
