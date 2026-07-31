using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Visibility;

/// <summary>
/// The security boundary (DOMAIN_MODEL §7): every session/poll read <em>and</em> write by a
/// registered viewer resolves here and fails closed — no match is reported as not found, never as
/// forbidden. The four rules are applied in their stated order, with the block filter taking
/// precedence over the other three.
/// </summary>
/// <remarks>
/// Guest link resolution is the §5.4 exception and deliberately does not come through here: a
/// bearer token is its own proof (<see cref="GuestLinkResolution"/>).
/// </remarks>
public static class VisibilityEngine
{
    public static VisibilityResult Resolve(VisibilityTarget target, Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(viewer);

        // Rule 4 first: a blocked pair's target does not exist for the other (D-065). The only
        // coexistence is a third-party target, where the organizer is neither of the two.
        if (viewer.IsBlockedWith(target.OrganizerId))
        {
            return VisibilityResult.NotFound;
        }

        // Rule 1: direct access.
        if (viewer.Id == target.OrganizerId || target.HasPendingRequestFrom(viewer.Id))
        {
            return VisibilityResult.Direct;
        }

        if (target.TryFindMember(viewer.Id, out VisibilityMember own) && own.KeepsDirectAccess)
        {
            return VisibilityResult.Direct;
        }

        // Rule 3: a private target is rule 1 only — the privacy-flip retained set (D-043/D-047).
        if (target.IsPrivate)
        {
            return VisibilityResult.NotFound;
        }

        // Rule 2: inherited access. The organizer extends regardless of their own attendance (D-085).
        if (viewer.IsFriendOf(target.OrganizerId))
        {
            return VisibilityResult.Inherited(target.OrganizerId);
        }

        foreach (VisibilityMember member in target.Members)
        {
            if (member.ExtendsAccess
                && viewer.IsFriendOf(member.UserId)
                && !viewer.IsBlockedWith(member.UserId))
            {
                return VisibilityResult.Inherited(member.UserId);
            }
        }

        return VisibilityResult.NotFound;
    }

    /// <summary>
    /// The fail-closed gate the application layer puts in front of every read and write: an
    /// invisible target raises <see cref="DomainError.NotFound"/>, indistinguishable from one that
    /// was never created.
    /// </summary>
    public static VisibilityResult Require(VisibilityTarget target, Viewer viewer)
    {
        VisibilityResult result = Resolve(target, viewer);

        if (!result.IsVisible)
        {
            throw new DomainException(DomainError.NotFound, "No such session or poll.");
        }

        return result;
    }

    public static VisibilityResult ResolveSession(Session session, Viewer viewer) =>
        Resolve(SessionVisibility.Describe(session), viewer);

    public static VisibilityResult RequireSession(Session session, Viewer viewer) =>
        Require(SessionVisibility.Describe(session), viewer);

    public static VisibilityResult ResolvePoll(Poll poll, Viewer viewer) =>
        Resolve(PollVisibility.Describe(poll), viewer);

    public static VisibilityResult RequirePoll(Poll poll, Viewer viewer) =>
        Require(PollVisibility.Describe(poll), viewer);
}
