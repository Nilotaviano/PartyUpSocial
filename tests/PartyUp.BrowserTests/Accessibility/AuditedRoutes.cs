namespace PartyUp.BrowserTests.Accessibility;

/// <summary>
/// One entry per audited route. <paramref name="PathTemplate"/> carries `{sessionId}`,
/// `{username}` or `{code}` placeholders where the route needs a live entity id;
/// <see cref="Accessibility.AxeAuditTests"/> resolves them against a shared fixture context before
/// navigating. A route needing a token this fixture cannot mint on demand (magic link, password
/// reset, email verification/change, external-auth callback) or a mid-onboarding account state
/// (the six `/onboarding/*` sub-steps, which <see cref="PlaywrightFixture.RegisterAndOnboardAsync"/>
/// drives straight through rather than pausing on) is out of S1's seed — "reachable in the fixture"
/// (PHASE5_DESIGN.md §9) excludes what the fixture has no way to reach.
/// </summary>
public sealed record AuditedRoute(
    string Name,
    string PathTemplate,
    bool RequiresGuestLink = false);

/// <summary>
/// Checked-in route inventory for the S1 axe lane (A-082, PHASE5_DESIGN.md §9, §11 "Browser lane
/// (S1)"). Seeded with every Phase 4 route reachable in <see cref="PlaywrightFixture"/> — registered
/// and guest surfaces alike. Each UI slice from S6 onward appends its own new routes here (design
/// §12 serialization: `AuditedRoutes.cs` is S1 → S6 → S7); no Phase 5 route may carry an
/// <see cref="AxeExclusions"/> entry (P5-AXE-02).
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
        new AuditedRoute("guest-invalid", "/guest/invalid"),
        new AuditedRoute("app", "/app"),
        new AuditedRoute("friends", "/friends"),
        new AuditedRoute("friends-import-steam", "/friends/import/steam"),
        new AuditedRoute("profile", "/u/{username}"),
        new AuditedRoute("add-friend", "/add/{code}"),
        new AuditedRoute("settings-profile", "/settings/profile"),
        new AuditedRoute("settings-account", "/settings/account"),
        new AuditedRoute("settings-blocked", "/settings/blocked"),
        new AuditedRoute("sessions-new", "/sessions/new"),
        new AuditedRoute("sessions-upcoming", "/sessions/upcoming"),
        new AuditedRoute("sessions-history", "/sessions/history"),
        new AuditedRoute("session-detail", "/sessions/{sessionId}"),
        new AuditedRoute("session-edit", "/sessions/{sessionId}/edit"),
        new AuditedRoute("session-invite", "/sessions/{sessionId}/invite"),
        new AuditedRoute("guest-session", "/guest/{guestLinkId}", RequiresGuestLink: true),
    ];
}
