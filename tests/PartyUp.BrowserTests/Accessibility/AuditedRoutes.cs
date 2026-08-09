namespace PartyUp.BrowserTests.Accessibility;

/// <summary>
/// Which of the fixture's two guest links a <see cref="AuditedRoute.RequiresGuestLink"/> route
/// resolves against — session-typed and poll-typed guest links share one route shape
/// (<c>/guest/{guestLinkId}</c>) but structurally different credentials (PHASE6_DESIGN.md §4), so
/// S6 adds this discriminator rather than a second boolean.
/// </summary>
public enum GuestLinkKind
{
    Session,
    Poll,
}

/// <summary>
/// One entry per audited route. <paramref name="PathTemplate"/> carries `{sessionId}`,
/// `{username}` or `{code}` placeholders where the route needs a live entity id;
/// <see cref="Accessibility.AxeAuditTests"/> resolves them against a shared fixture context before
/// navigating. <paramref name="OnboardingStepKey"/> is set only for the seven `/onboarding*` routes
/// (round-1 F5) — those pages cannot be reached by direct navigation at all (see
/// <see cref="Accessibility.AxeAuditTests"/>'s remarks), so their finding is captured during the
/// fixture's own registration walk instead of a fresh <c>GotoAsync</c>. A route needing a token this
/// fixture cannot mint on demand (magic link, password reset, email verification/change,
/// external-auth callback) stays out of S1's seed — "reachable in the fixture" (PHASE5_DESIGN.md §9)
/// excludes only what the fixture genuinely has no way to reach.
/// </summary>
public sealed record AuditedRoute(
    string Name,
    string PathTemplate,
    bool RequiresGuestLink = false,
    string? OnboardingStepKey = null,
    GuestLinkKind GuestLinkKind = GuestLinkKind.Session);

/// <summary>
/// Checked-in route inventory for the S1 axe lane (A-082, PHASE5_DESIGN.md §9, §11 "Browser lane
/// (S1)"). Seeded with every Phase 4 route reachable in <see cref="PlaywrightFixture"/> — registered,
/// onboarding and guest surfaces alike. Each UI slice from S6 onward appends its own new routes here
/// (design §12 serialization: `AuditedRoutes.cs` is S1 → S6 → S7); no Phase 5 route may carry an
/// <see cref="AxeExclusions"/> entry (P5-AXE-02) — enforced against the frozen S1 allowlist in
/// `AxeExclusionListTests`, not against this list, because this list itself grows every later slice.
/// </summary>
public static class AuditedRoutes
{
    public static IReadOnlyList<AuditedRoute> All { get; } =
    [
        new AuditedRoute("welcome", "/welcome"),
        new AuditedRoute("age-gate", "/age-gate"),
        new AuditedRoute("login", "/login"),
        new AuditedRoute("register", "/register"),
        new AuditedRoute("account-deletion", "/account-deletion"),
        new AuditedRoute("account-deletion-confirm", "/account-deletion/confirm"),
        new AuditedRoute("guest-invalid", "/guest/invalid"),
        // The bare index route redirects immediately to whichever step a fresh draft allows —
        // "username" for the account this fixture walks — so it renders the identical DOM the
        // "username" capture already covers; both entries key the same captured finding.
        new AuditedRoute("onboarding", "/onboarding", OnboardingStepKey: "username"),
        new AuditedRoute("onboarding-username", "/onboarding/username", OnboardingStepKey: "username"),
        new AuditedRoute("onboarding-display-name", "/onboarding/display-name", OnboardingStepKey: "display-name"),
        new AuditedRoute("onboarding-avatar", "/onboarding/avatar", OnboardingStepKey: "avatar"),
        new AuditedRoute("onboarding-time-zone", "/onboarding/time-zone", OnboardingStepKey: "time-zone"),
        new AuditedRoute("onboarding-link-platforms", "/onboarding/link-platforms", OnboardingStepKey: "link-platforms"),
        new AuditedRoute("onboarding-friends", "/onboarding/friends", OnboardingStepKey: "friends"),
        new AuditedRoute("app", "/app"),
        new AuditedRoute("friends", "/friends"),
        new AuditedRoute("friends-import-steam", "/friends/import/steam"),
        new AuditedRoute("profile", "/u/{username}"),
        new AuditedRoute("add-friend", "/add/{code}"),
        new AuditedRoute("settings-profile", "/settings/profile"),
        new AuditedRoute("settings-account", "/settings/account"),
        new AuditedRoute("settings-blocked", "/settings/blocked"),
        new AuditedRoute("sessions-new", "/sessions/new"),
        new AuditedRoute("calendar", "/calendar"),
        new AuditedRoute("calendar-agenda", "/calendar/agenda"),
        new AuditedRoute("find-a-time", "/find-a-time"),
        new AuditedRoute("discover", "/discover"),
        new AuditedRoute("sessions-upcoming", "/sessions/upcoming"),
        new AuditedRoute("sessions-history", "/sessions/history"),
        new AuditedRoute("session-detail", "/sessions/{sessionId}"),
        new AuditedRoute("session-edit", "/sessions/{sessionId}/edit"),
        new AuditedRoute("session-invite", "/sessions/{sessionId}/invite"),
        new AuditedRoute("guest-session", "/guest/{guestLinkId}", RequiresGuestLink: true),
        // Phase 6 S5 (PHASE6_DESIGN.md §5/§6, ticket-P6-S5): create + list (both
        // bands) + read-only detail. Voting grid/suggest/guest voting routes stay
        // out until S6.
        new AuditedRoute("polls-new", "/polls/new"),
        new AuditedRoute("polls", "/polls"),
        new AuditedRoute("poll-detail", "/polls/{pollId}"),
        // Phase 6 S6 (PHASE6_DESIGN.md §5, ticket-P6-S6): the voting grid, suggest
        // affordance and guest-management panel now render live on poll-detail
        // (the entry above), and the guest pollView branch on the shared
        // `/guest/{guestLinkId}` route resolves against a poll-typed link.
        new AuditedRoute("guest-poll", "/guest/{guestLinkId}", RequiresGuestLink: true, GuestLinkKind: GuestLinkKind.Poll),
        // Phase 6 S7 (PHASE6_DESIGN.md §5/§6.1, ticket-P6-S7): the organizer's awaitingSelection
        // selection controls + finalization preview only render past the poll's deadline — a
        // second, backdated poll fixture reaches that state (the S5 poll above never does).
        new AuditedRoute("poll-detail-awaiting-selection", "/polls/{pollAwaitingSelectionId}"),
        // Phase 7 S5 (PHASE7_DESIGN.md §5/§6, ticket-P7-S5): the notification center.
        new AuditedRoute("notifications", "/notifications"),
        // Phase 7 S6 (PHASE7_DESIGN.md §5/§6, ticket-P7-S6): push/reminder/mute preferences.
        new AuditedRoute("settings-notifications", "/settings/notifications"),
    ];
}
