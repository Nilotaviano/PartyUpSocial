using PartyUp.Application.Auth;

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
/// fixture's own registration walk instead of a fresh <c>GotoAsync</c>. Token-bearing routes were
/// out of S1's seed because the fixture could not mint on demand; Phase 8 S7 closes that under Q4's
/// ratified pre-hashed-row mechanism (<paramref name="TokenPurpose"/> below), leaving
/// <c>/auth/callback</c> as the one route with no repo-side audit at all.
/// </summary>
/// <param name="SignedOut">
/// Phase 8 S7: the route's real audience is a signed-out visitor arriving from a mailed link, so it
/// is scanned in a throwaway anonymous context rather than on the organizer's authenticated one —
/// an authenticated session changes what the app chrome renders around these pages.
/// </param>
/// <param name="RequiresAdmin">
/// Phase 8 S3 (PHASE8_DESIGN.md §3): the route is behind <c>RequireAdmin</c>, which answers 404 to
/// everyone else — <see cref="Accessibility.AxeAuditTests"/> navigates it on the dedicated admin audit
/// account's context rather than the shared organizer's, or it would scan the fail-closed branch.
/// </param>
/// <param name="TokenPurpose">
/// Phase 8 S7 (PHASE8_DESIGN.md §2 Q4, ratified): the route only renders its real DOM with a
/// security token in the URL fragment. <see cref="Accessibility.AxeAuditTests"/> mints one over the
/// fixture's own Npgsql connection (<c>PlaywrightFixture.MintAuthTokenAsync</c> — a pre-hashed
/// <c>auth_tokens</c> row in exactly <c>SecurityTokenService.IssueAsync</c>'s shape) and navigates
/// <c>{path}#token={raw}</c> in a throwaway, signed-out context, the way a real mailed link arrives.
/// A fresh token per case: tokens are single-use.
/// </param>
public sealed record AuditedRoute(
    string Name,
    string PathTemplate,
    bool RequiresGuestLink = false,
    string? OnboardingStepKey = null,
    GuestLinkKind GuestLinkKind = GuestLinkKind.Session,
    bool RequiresAdmin = false,
    AuthTokenPurpose? TokenPurpose = null,
    bool SignedOut = false);

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
    /// <summary>
    /// Phase 8 S7: the theory below keys on this name rather than on the record itself, so each row
    /// carries a serializable argument and <c>--filter FullyQualifiedName~{name}</c> selects exactly
    /// one route's two theme cases. With the record as the argument, vstest could only ever run the
    /// whole ~50-route theory — there was no way to re-run a single finding.
    /// </summary>
    public static AuditedRoute ByName(string name) =>
        All.SingleOrDefault(route => string.Equals(route.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"No audited route is named '{name}'.");

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
        // Phase 8 S3 (PHASE8_DESIGN.md §3 S3, Q2 ratified): the three URL-only admin routes. No
        // navigation entry renders for them anywhere, so they are reached by direct navigation on
        // the admin audit account's own context.
        new AuditedRoute("admin-dashboard", "/admin", RequiresAdmin: true),
        new AuditedRoute("admin-users", "/admin/users", RequiresAdmin: true),
        new AuditedRoute("admin-user-detail", "/admin/users/{adminUserId}", RequiresAdmin: true),
        // Phase 8 S5 (PHASE8_DESIGN.md §3 S5): the three legal documents, public like
        // `/account-deletion` above. They also carry the app footer, which renders on every
        // unauthenticated surface already listed here and inside the authenticated shell, so the
        // footer is audited wherever it appears rather than as an entry of its own.
        new AuditedRoute("privacy", "/privacy"),
        new AuditedRoute("terms", "/terms"),
        new AuditedRoute("community-standards", "/community-standards"),
        // Phase 8 S7 (PHASE8_DESIGN.md §3 S7): the delta against App.tsx's route table — the two
        // plainly-navigable routes the S1..S5 seed missed, then the token-gated set under Q4.
        //
        // Plainly navigable, organizer-authenticated like every other in-app route above.
        new AuditedRoute("poll-invite", "/polls/{pollId}/invite"),
        // `/email-unsubscribe` reads its token from the URL fragment and renders only the
        // invalid-link branch without one, so both states are audited — they are different DOM,
        // not the same page twice.
        new AuditedRoute("email-unsubscribe-invalid", "/email-unsubscribe", SignedOut: true),
        new AuditedRoute(
            "email-unsubscribe-token",
            "/email-unsubscribe",
            SignedOut: true,
            TokenPurpose: AuthTokenPurpose.EmailUnsubscribe),
        // The three `TokenForm` routes (`AuthScreens.tsx`) render their form on mount and consume
        // the token only on submit, so a minted token renders inertly: the audit sees the real
        // token-bearing DOM and the backing account is never mutated. That is why one organizer
        // account backs all of these rather than a dedicated registration (Q4's registration
        // budget: every extra one costs a full multi-page walk through the registration UI).
        new AuditedRoute(
            "auth-reset",
            "/auth/reset",
            SignedOut: true,
            TokenPurpose: AuthTokenPurpose.PasswordReset),
        new AuditedRoute(
            "auth-verify-email",
            "/auth/verify-email",
            SignedOut: true,
            TokenPurpose: AuthTokenPurpose.EmailVerification),
        new AuditedRoute(
            "auth-email-change",
            "/auth/email-change",
            SignedOut: true,
            TokenPurpose: AuthTokenPurpose.EmailChange),
        // `/auth/magic` and `/auth/confirm-identity` differ from the three above: both consume the
        // token in a mount effect and `navigate(..., { replace: true })` away on success, so a
        // minted token would leave axe scanning the *destination* (`/app`, `/settings/account`) —
        // routes already audited above — and report nothing about these two. Their own DOM is the
        // no-token invalid-link branch, which is deterministic, so that is what is audited here.
        new AuditedRoute("auth-magic-invalid", "/auth/magic", SignedOut: true),
        new AuditedRoute("auth-confirm-identity-invalid", "/auth/confirm-identity", SignedOut: true),
        // `/auth/callback` carries NO entry, by decision rather than omission (Q4's documented
        // manual-audit disposition): it is the external-auth redirect leg. It reads `reauth`/error
        // state out of the fragment an external identity provider put there and immediately routes
        // onward; there is no fixture-reachable state in which it renders a stable page of its own,
        // and reaching a genuine one needs a real provider round trip. Recorded as a manual audit
        // against D-139 in TRACEABILITY.md and in docs/WCAG_AUDIT_PHASE8.md, not silently skipped.
    ];
}
