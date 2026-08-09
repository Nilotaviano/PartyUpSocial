namespace PartyUp.BrowserTests.Accessibility;

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
    string? OnboardingStepKey = null);

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
    ];
}
