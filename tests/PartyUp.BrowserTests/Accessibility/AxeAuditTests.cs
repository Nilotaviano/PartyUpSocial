using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Accessibility;

/// <summary>
/// A-082/P5-AXE-02 (PHASE5_DESIGN.md §9, §11 "Browser lane (S1)"): axe-core over every checked-in
/// Phase 4 route (<see cref="AuditedRoutes"/>) at the shipped WCAG 2.2 AA tag set — any violation of
/// any impact fails unless it is a dated, route+rule-scoped entry in <see cref="AxeExclusions"/>.
/// <para>
/// The seven <c>/onboarding*</c> routes (round-1 F5) cannot be reached by a fresh
/// <c>Page.GotoAsync</c> at all: <c>OnboardingWizard.tsx</c> redirects every one of its routes to
/// the <c>friends</c> step once an account's onboarding is complete, and redirects a request past
/// whatever step the account's own draft has reached back to that step otherwise — there is no
/// direct-navigation way to land on, say, <c>/onboarding/avatar</c> without an account that is
/// genuinely mid-onboarding at exactly that step. <see cref="GetContextAsync"/> therefore captures
/// each step's axe result live, from inside <see cref="PlaywrightFixture.RegisterAndOnboardAsync"/>'s
/// own walk through the wizard, via its <c>onOnboardingStep</c> callback — the same real walk every
/// other registration in this suite already performs, just observed at each pause. The per-route
/// theory below reads those pre-captured results for onboarding routes instead of navigating.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class AxeAuditTests(PlaywrightFixture fixture)
{
    private static readonly string[] WcagTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
    private static readonly JsonSerializerOptions DumpJsonOptions = new() { WriteIndented = true };

    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static AuditContext? _context;

    private sealed record AuditContext(
        RegisteredUser Organizer,
        string SessionId,
        string GuestLinkUrl,
        string OwnFriendCode,
        IReadOnlyDictionary<string, AxeResultItem[]> OnboardingFindings);

    /// <summary>
    /// One real registered account, one private session and one guest link — enough live entity ids
    /// to resolve every dynamic-segment route in <see cref="AuditedRoutes"/>. Built once and shared
    /// across every axe fact the same way the flow-test classes share their setup.
    /// <para>
    /// Deliberately a single registration, and a <b>private</b> session (D-099 exempts private
    /// sessions from needing an invited friend): the flow-test classes sharing this fixture's one
    /// API process already spend three of the five allowed `register-ip` calls
    /// (`RateLimitPolicies.cs`, 5/user-IP/hour) on <c>SessionRegisteredFlowTests</c> and one more on
    /// <c>SessionGuestFlowTests</c>. A second axe-only account would push the shared process to six
    /// registrations inside the same rolling hour and 429 — this class stays at exactly one so the
    /// whole collection's total (five) never crosses the ceiling. `{username}`/`{code}` routes below
    /// resolve against the organizer's own identity rather than a second party's.
    /// </para>
    /// <para>
    /// Round-1 F9 (record only): this leaves <b>zero</b> `register-ip` headroom for any later
    /// slice's own browser tests that need a fresh account in this same collection — the five
    /// registrations already shipped (three here, one in each flow class) are the whole hourly
    /// budget. A slice that needs a sixth registration must either reuse an account a `[Collection]`
    /// sibling already created or the dimension itself must widen (`RateLimitPolicies.cs`, not in
    /// this ticket's write set).
    /// </para>
    /// </summary>
    private async Task<AuditContext> GetContextAsync()
    {
        if (_context is not null)
        {
            return _context;
        }

        await SetupLock.WaitAsync();
        try
        {
            if (_context is not null)
            {
                return _context;
            }

            Dictionary<string, AxeResultItem[]> onboardingFindings = new(StringComparer.Ordinal);
            RegisteredUser organizer = await fixture.RegisterAndOnboardAsync(
                await fixture.NewContextAsync(),
                "axeorganizer",
                "Axe Organizer",
                onOnboardingStep: async (step, page) =>
                {
                    onboardingFindings[step] = await RunAxeAsync(page);
                });

            await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");
            await organizer.Page.Locator("#field-title").FillAsync("Axe audit session");
            await organizer.Page.Locator("#field-start").FillAsync(
                DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));
            await organizer.Page.Locator("#field-duration").FillAsync("60");
            await organizer.Page.Locator("#field-min").FillAsync("1");
            await organizer.Page.Locator("#field-max").FillAsync("4");
            await organizer.Page.GetByLabel("Private session").CheckAsync();
            await organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Create session" }).ClickAsync();
            await organizer.Page.WaitForURLAsync(url => Regex.IsMatch(url, @"/sessions/[0-9a-fA-F-]{36}$"));
            string sessionId = new Uri(organizer.Page.Url).Segments[^1];

            await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId + "/invite");
            await organizer.Page.Locator("#guest-link-count").FillAsync("1");
            await organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Create guest link" }).ClickAsync();
            string guestLinkUrl = await organizer.Page.Locator("span.font-mono").First.InnerTextAsync();

            // GetOwnFriendCodeAsync reads the ShareCard, only rendered while the account has zero
            // friends (PlaywrightFixture doc comment) — true here since this account never befriends
            // anyone. Read after the invite step so this navigation doesn't disturb it.
            string ownFriendCode = await PlaywrightFixture.GetOwnFriendCodeAsync(organizer.Page);

            // S8 hardening: seed a real availability series so the `/calendar` scan below exercises
            // the grid with a populated `myAvailability` layer (a busy cell, its ARIA name, in-grid
            // block semantics) rather than the empty-grid DOM every prior run scanned — the session
            // created above already populates the `sessions` layer for this same account.
            await SeedWeeklyAvailabilityAsync(organizer.Page);

            _context = new AuditContext(organizer, sessionId, guestLinkUrl, ownFriendCode, onboardingFindings);
            return _context;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    /// <summary>
    /// No web UI authors a recurring series in Phase 5's write set (server-side only), so — exactly
    /// like <c>AvailabilityCalendarFlowTests.SeedWeeklySeriesAsync</c> — this seeds one through the
    /// same authenticated origin the SPA itself calls, from inside the page (shares the session
    /// cookie; mints its own antiforgery token the way <c>web/src/api/client.ts</c> does), never a
    /// second HTTP client or a second registration.
    /// </summary>
    private static async Task SeedWeeklyAvailabilityAsync(IPage page)
    {
        JsonElement result = await page.EvaluateAsync<JsonElement>(
            """
            async () => {
                const now = new Date();
                const pad = (n) => String(n).padStart(2, '0');
                const dayOfWeek = ((now.getDay() + 6) % 7) + 1;
                const effectiveFrom =
                    `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
                const tokenResponse = await fetch('/api/v1/auth/antiforgery', { credentials: 'include' });
                const { token } = await tokenResponse.json();
                const response = await fetch('/api/v1/me/availability/series', {
                    method: 'POST',
                    credentials: 'include',
                    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
                    body: JSON.stringify({
                        dayOfWeek,
                        startTime: '10:00',
                        durationMinutes: 60,
                        effectiveFrom,
                        effectiveUntil: null,
                    }),
                });
                if (!response.ok) {
                    throw new Error('Availability seed failed: ' + response.status);
                }
                return response.json();
            }
            """);

        if (result.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Availability series seed returned no data.");
        }
    }

    private static async Task<AxeResultItem[]> RunAxeAsync(IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        AxeResult result = await page.RunAxe(new AxeRunOptions { RunOnly = RunOnlyOptions.Tags(WcagTags) });
        return result.Violations;
    }

    public static IEnumerable<object[]> Routes() => AuditedRoutes.All.Select(route => new object[] { route });

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryAuditedRouteHasNoWcagViolationOutsideTheDatedExclusionList_D139(AuditedRoute route)
    {
        AuditContext context = await GetContextAsync();

        AxeResultItem[] violations;
        if (route.OnboardingStepKey is not null)
        {
            // Captured live during GetContextAsync's registration walk — see this class's own
            // remarks and PlaywrightFixture.RegisterAndOnboardAsync's onOnboardingStep parameter.
            violations = context.OnboardingFindings[route.OnboardingStepKey];
        }
        else
        {
            IBrowserContext? guestContext = null;
            IPage page;
            if (route.RequiresGuestLink)
            {
                // Own short-lived context per guest-route audit (never the organizer's persistent
                // one) — closed below regardless of outcome so the route loop doesn't accumulate
                // open browser contexts across the run.
                guestContext = await fixture.NewContextAsync();
                page = await guestContext.NewPageAsync();
                await page.GotoAsync(context.GuestLinkUrl);
            }
            else
            {
                string path = route.PathTemplate
                    .Replace("{sessionId}", context.SessionId, StringComparison.Ordinal)
                    .Replace("{username}", context.Organizer.Username, StringComparison.Ordinal)
                    .Replace("{code}", context.OwnFriendCode, StringComparison.Ordinal);
                page = context.Organizer.Page;
                await page.GotoAsync(PlaywrightFixture.WebBaseUrl + path);
            }

            try
            {
                violations = await RunAxeAsync(page);
            }
            finally
            {
                if (guestContext is not null)
                {
                    await guestContext.CloseAsync();
                }
            }
        }

        HashSet<string> excludedRuleIds = AxeExclusions.Entries
            .Where(entry => entry.RouteName == route.Name)
            .Select(entry => entry.RuleId)
            .ToHashSet(StringComparer.Ordinal);

        AxeResultItem[] unexpected = violations
            .Where(violation => !excludedRuleIds.Contains(violation.Id))
            .ToArray();

        if (unexpected.Length > 0)
        {
            Directory.CreateDirectory(AxeArtifactsDirectory);
            string dumpPath = Path.Combine(AxeArtifactsDirectory, $"{route.Name}.json");
            await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(unexpected, DumpJsonOptions));
        }

        Assert.True(
            unexpected.Length == 0,
            $"Route '{route.Name}' has {unexpected.Length} un-excluded axe violation(s): " +
            string.Join(", ", unexpected.Select(v => $"{v.Id} ({v.Impact})")) +
            $". Details in {AxeArtifactsDirectory}.");
    }

    /// <summary>
    /// Round-1 F8 (NOTE, detection-power proof): the 29/29-clean result above is only meaningful if
    /// this harness can actually catch a real violation — this negative control proves it can,
    /// independent of the app, by scanning a deliberately inaccessible DOM (an unlabeled input and
    /// an <c>&lt;img&gt;</c> with no <c>alt</c>) and asserting axe reports it. If this fact ever
    /// goes green-without-violations, the harness itself is broken, not the app.
    /// </summary>
    [Fact]
    public async Task NegativeControl_DeliberatelyViolatingDomIsReportedByAxe_D139()
    {
        IBrowserContext context = await fixture.NewContextAsync();
        IPage page = await context.NewPageAsync();
        try
        {
            await page.SetContentAsync(
                "<html><body>" +
                "<input type=\"text\">" +
                "<img src=\"data:image/gif;base64,R0lGODlhAQABAAAAACw=\">" +
                "</body></html>");

            AxeResultItem[] violations = await RunAxeAsync(page);

            Assert.Contains(violations, v => v.Id is "label" or "image-alt");
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static string AxeArtifactsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "axe-results");
}

/// <summary>
/// The list-shape half of A-082/P5-AXE-02: not a suite over every entry, one gate over the list
/// itself. Every entry must name a real, dated, traceable finding, on a route that was part of the
/// S1 seed — enforced structurally so a future slice cannot silently widen the list instead of
/// shrinking it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AxeExclusionListTests
{
    /// <summary>
    /// Round-1 F4: this used to be a denylist of four route names someone had to remember to keep
    /// current — a route this literal didn't yet know about (any Phase 5 route, by construction)
    /// passed by default. Inverted to a closed allowlist instead, frozen to exactly the route names
    /// <c>AuditedRoutes.cs</c> seeded at S1 close: any entry naming a route NOT in this literal
    /// fails, including every one of S6/S7's own new Phase 5 routes, by default rather than by
    /// someone remembering to add them here. <see cref="AuditedRoutes"/> itself is deliberately NOT
    /// consulted for this half of the check — that list grows every later slice, so checking against
    /// its current contents would let a Phase 5 route in the moment its own slice adds it there.
    /// This constant must never gain a name after S1 close, no matter how <c>AuditedRoutes.cs</c>
    /// grows.
    /// </summary>
    private static readonly IReadOnlyList<string> S1SeededPhase4RouteNames =
    [
        "welcome", "age-gate", "login", "register",
        "account-deletion", "account-deletion-confirm", "guest-invalid",
        "onboarding", "onboarding-username", "onboarding-display-name", "onboarding-avatar",
        "onboarding-time-zone", "onboarding-link-platforms", "onboarding-friends",
        "app", "friends", "friends-import-steam", "profile", "add-friend",
        "settings-profile", "settings-account", "settings-blocked",
        "sessions-new", "sessions-upcoming", "sessions-history",
        "session-detail", "session-edit", "session-invite", "guest-session",
    ];

    [Fact]
    public void EntriesAreDatedRuleScopedTraceabilityCitedAndNeverOnAPhase5Route_D139()
    {
        HashSet<string> allowedRouteNames = new(S1SeededPhase4RouteNames, StringComparer.Ordinal);

        foreach (AxeExclusion entry in AxeExclusions.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.RuleId));
            Assert.True(
                DateOnly.TryParse(entry.DiscoveredOn, out _),
                $"'{entry.RouteName}'/'{entry.RuleId}' carries an unparsable discovery date '{entry.DiscoveredOn}'.");
            Assert.False(
                string.IsNullOrWhiteSpace(entry.TraceabilityRow),
                $"'{entry.RouteName}'/'{entry.RuleId}' carries no TRACEABILITY.md citation.");
            Assert.True(
                allowedRouteNames.Contains(entry.RouteName),
                $"'{entry.RouteName}' is not one of the S1-seeded Phase 4 routes; P5-AXE-02/A-082 " +
                "forbid any route outside that frozen set — including every Phase 5 route — from " +
                "ever entering this list.");
        }
    }
}
