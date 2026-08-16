using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Visibility;

/// <summary>
/// The security boundary (DOMAIN_MODEL §7): every session/poll read <em>and</em> write by a
/// registered viewer resolves here and fails closed — no match is reported as not found, never as
/// forbidden. The canonical order is rule 4 → rule 1 → level gate → the inheritance rule that level
/// admits (rule 2g for Groups, rule 2 for Public); the rule numbers are stable identifiers.
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

        // §7 level gate: Private is rule 1 only (D-043/D-047); Groups admits only
        // owners/Accepted members of a targeted group (rule 2g, D-198) — rule-2
        // inheritance is Public-only.
        if (target.Visibility == ContentVisibility.Private)
        {
            return VisibilityResult.NotFound;
        }

        if (target.Visibility == ContentVisibility.Groups)
        {
            foreach (FriendGroupId groupId in target.AudienceGroupIds)
            {
                if (viewer.IsInGroup(groupId))
                {
                    return VisibilityResult.Group(groupId);
                }
            }

            return VisibilityResult.NotFound;
        }

        // Exhaustiveness: only Public reaches rule 2. Any level outside the three defined values
        // (an EF-hydrated smallint or an unvalidated VisibilityTarget.Create call) fails closed
        // rather than falling through to inheritance (D-198).
        if (target.Visibility != ContentVisibility.Public)
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
    /// The throwing form of the fail-closed gate: an invisible target raises
    /// <see cref="DomainError.NotFound"/>, indistinguishable from one that was never created.
    /// Application gates are written as <c>Resolve(...).IsVisible</c> instead, mapping the miss to
    /// their own not-found code, so the absence of a <c>Require</c> call at a read or write says
    /// nothing about whether that path is gated — check for the <c>Resolve</c> form.
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
