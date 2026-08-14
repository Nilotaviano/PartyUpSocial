using System.Text.Json;
using Deque.AxeCore.Commons;
using Microsoft.Playwright;
using PartyUp.Application.Auth;

namespace PartyUp.BrowserTests.Accessibility;

/// <summary>
/// The theme dimension every <see cref="AuditedRoutes"/> entry is now scanned under (PHASE5_1_DESIGN.md
/// §4 slice 8) — both are reached by an explicit act (a pre-navigation seed, or the shipped theme
/// switch on an already-loaded page), never left to the browser's own default color scheme, so
/// neither audit run's result depends on an unstated ambient preference.
/// </summary>
public enum AuditedTheme
{
    Light,
    Dark,
}

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
/// genuinely mid-onboarding at exactly that step. <see cref="AxeAuditWorld"/> therefore captures
/// each step's axe result live, from inside <see cref="PlaywrightFixture.RegisterAndOnboardAsync"/>'s
/// own walk through the wizard, via its <c>onOnboardingStep</c> callback — the same real walk every
/// other registration in this suite already performs, just observed at each pause. The per-route
/// theory below reads those pre-captured results for onboarding routes instead of navigating.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(AxeRouteSuite.Name)]
public sealed class AxeAuditTests(PlaywrightFixture fixture)
{
    /// <summary>
    /// This collection's own signed-in contexts. The audit world (<see cref="AxeAuditWorld"/>) is
    /// shared with the interaction collection running beside this one, but a browser context is not
    /// shareable: both collections seed <c>partyup:theme</c> into <c>localStorage</c> before a
    /// scan's first navigation, and one shared context would let either collection repaint the
    /// other's page mid-scan. Two sign-ins — the same organizer and admin accounts the world already
    /// created, never new ones — buy that isolation for the whole collection.
    /// </summary>
    private sealed record AuditSurfaces(IBrowserContext Organizer, IBrowserContext Admin);

    private static readonly SemaphoreSlim SurfacesLock = new(1, 1);

    private static Task<AuditSurfaces>? _surfacesTask;

    private async Task<AuditSurfaces> GetSurfacesAsync()
    {
        if (_surfacesTask is not null)
        {
            return await _surfacesTask;
        }

        await SurfacesLock.WaitAsync();
        try
        {
            _surfacesTask ??= BuildSurfacesAsync();
        }
        finally
        {
            SurfacesLock.Release();
        }

        return await _surfacesTask;
    }

    private async Task<AuditSurfaces> BuildSurfacesAsync()
    {
        AxeAuditWorld.AuditContext context = await AxeAuditWorld.GetContextAsync(fixture);
        return new AuditSurfaces(
            await fixture.SignInNewContextAsync(context.Organizer.Username),
            await fixture.SignInNewContextAsync(context.Admin.Username));
    }

    /// <summary>
    /// Rows carry the route's <b>name</b>, not the record: a string is a serializable theory
    /// argument, so vstest's <c>FullyQualifiedName</c> for each row contains it and a single route's
    /// light+dark pair can be re-run on its own (`--filter FullyQualifiedName~auth-reset`). With the
    /// record itself as the argument the only runnable unit was the whole ~50-route theory.
    /// </summary>
    public static IEnumerable<object[]> Routes() =>
        from route in AuditedRoutes.All
        from theme in new[] { AuditedTheme.Light, AuditedTheme.Dark }
        select new object[] { route.Name, theme };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryAuditedRouteHasNoWcagViolationOutsideTheDatedExclusionList_D139(
        string routeName,
        AuditedTheme theme)
    {
        AuditedRoute route = AuditedRoutes.ByName(routeName);
        AxeAuditWorld.AuditContext context = await AxeAuditWorld.GetContextAsync(fixture);

        AxeResultItem[] violations = route.OnboardingStepKey is not null
            // Captured live during GetContextAsync's registration walk — see this class's own
            // remarks and PlaywrightFixture.RegisterAndOnboardAsync's onOnboardingStep parameter.
            ? context.OnboardingFindings[(route.OnboardingStepKey, theme)]
            : (await GetRouteFindingsAsync(route, context))[theme];

        HashSet<string> excludedRuleIds = AxeExclusions.Entries
            .Where(entry => entry.RouteName == route.Name)
            .Select(entry => entry.RuleId)
            .ToHashSet(StringComparer.Ordinal);

        AxeResultItem[] unexpected = violations
            .Where(violation => !excludedRuleIds.Contains(violation.Id))
            .ToArray();

        if (unexpected.Length > 0)
        {
            Directory.CreateDirectory(AxeAuditWorld.AxeArtifactsDirectory);
            string dumpPath = Path.Combine(AxeAuditWorld.AxeArtifactsDirectory, $"{route.Name}.{AxeAuditWorld.ThemeValue(theme)}.json");
            await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(unexpected, AxeAuditWorld.DumpJsonOptions));
        }

        Assert.True(
            unexpected.Length == 0,
            $"Route '{route.Name}' ({AxeAuditWorld.ThemeValue(theme)}) has {unexpected.Length} un-excluded axe " +
            "violation(s): " + string.Join(", ", unexpected.Select(v => $"{v.Id} ({v.Impact})")) +
            $". Details in {AxeAuditWorld.AxeArtifactsDirectory}.");
    }

    /// <summary>
    /// One navigation per route, both themes scanned off it — the light/dark pair are two
    /// <em>readings</em> of one rendered page, not two page loads, so a route costs one navigation
    /// instead of two. Each theory row still asserts only its own theme's findings, keeps its own
    /// dump file and names the theme in its failure message, so which cell failed is unchanged.
    /// <para>
    /// The cache holds the in-flight <see cref="Task{TResult}"/> (the
    /// <see cref="GetContextAsync"/> idiom): whichever of a route's two rows runs first performs the
    /// scan, the other awaits the same attempt and observes the same exception if it threw.
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim RouteFindingsLock = new(1, 1);

    private static readonly Dictionary<string, Task<IReadOnlyDictionary<AuditedTheme, AxeResultItem[]>>>
        RouteFindings = new(StringComparer.Ordinal);

    private async Task<IReadOnlyDictionary<AuditedTheme, AxeResultItem[]>> GetRouteFindingsAsync(
        AuditedRoute route,
        AxeAuditWorld.AuditContext context)
    {
        Task<IReadOnlyDictionary<AuditedTheme, AxeResultItem[]>>? scan;
        await RouteFindingsLock.WaitAsync();
        try
        {
            if (!RouteFindings.TryGetValue(route.Name, out scan))
            {
                scan = ScanBothThemesAsync(route, context);
                RouteFindings[route.Name] = scan;
            }
        }
        finally
        {
            RouteFindingsLock.Release();
        }

        return await scan;
    }

    private async Task<IReadOnlyDictionary<AuditedTheme, AxeResultItem[]>> ScanBothThemesAsync(
        AuditedRoute route,
        AxeAuditWorld.AuditContext context)
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
            await AxeAuditWorld.SeedThemeBeforeFirstNavigationAsync(page, AuditedTheme.Light);
            string guestLinkUrl = route.GuestLinkKind == GuestLinkKind.Poll
                ? context.PollGuestLinkUrl
                : context.GuestLinkUrl;
            await page.GotoAsync(guestLinkUrl);
        }
        else if (route.SignedOut)
        {
            // Phase 8 S7 / Q4: a mailed link opens in whatever browser the recipient has, signed
            // out — so these are audited in their own throwaway context. One minted token covers
            // both themes because they share this one navigation; the token is never redeemed a
            // second time. Closed in the finally, like the guest contexts above.
            guestContext = await fixture.NewContextAsync();
            page = await guestContext.NewPageAsync();
            await AxeAuditWorld.SeedThemeBeforeFirstNavigationAsync(page, AuditedTheme.Light);

            string url = PlaywrightFixture.WebBaseUrl + route.PathTemplate;
            if (route.TokenPurpose is AuthTokenPurpose purpose)
            {
                string rawToken = await fixture.MintAuthTokenAsync(
                    purpose,
                    context.TokenSubjectId,
                    context.TokenSubjectEmail);
                url += "#token=" + rawToken;
            }

            await page.GotoAsync(url);
        }
        else
        {
            string path = route.PathTemplate
                .Replace("{sessionId}", context.SessionId, StringComparison.Ordinal)
                .Replace("{username}", context.Organizer.Username, StringComparison.Ordinal)
                .Replace("{code}", context.OwnFriendCode, StringComparison.Ordinal)
                .Replace("{pollId}", context.PollId, StringComparison.Ordinal)
                .Replace(
                    "{pollAwaitingSelectionId}",
                    context.PollAwaitingSelectionId,
                    StringComparison.Ordinal)
                .Replace("{adminUserId}", context.AdminUserId, StringComparison.Ordinal);
            // A fresh tab on the account's own authenticated context — the admin audit account
            // for a `RequiresAdmin` route, the organizer otherwise (never either account's
            // persistent page) — shares its cookies so the route still renders signed-in, but
            // never navigated before, so the theme seed below is guaranteed to land before this
            // page's first paint. Closed in the finally below so the route loop doesn't accumulate
            // open tabs on that context.
            AuditSurfaces surfaces = await GetSurfacesAsync();
            page = route.RequiresAdmin
                ? await surfaces.Admin.NewPageAsync()
                : await surfaces.Organizer.NewPageAsync();
            await AxeAuditWorld.SeedThemeBeforeFirstNavigationAsync(page, AuditedTheme.Light);
            await page.GotoAsync(PlaywrightFixture.WebBaseUrl + path);
        }

        try
        {
            await AxeAuditWorld.AssertThemeAppliedAsync(page, AuditedTheme.Light);
            AxeResultItem[] light = await AxeAuditWorld.RunAxeAsync(page);

            await SwitchThemeAsync(page, AuditedTheme.Dark);
            AxeResultItem[] dark = await AxeAuditWorld.RunAxeAsync(page);

            return new Dictionary<AuditedTheme, AxeResultItem[]>
            {
                [AuditedTheme.Light] = light,
                [AuditedTheme.Dark] = dark,
            };
        }
        finally
        {
            if (guestContext is not null)
            {
                await guestContext.CloseAsync();
            }
            else
            {
                await page.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Repaints an already-loaded page in the other theme, exactly the way the shipped app does it:
    /// through <c>ThemeToggle</c>'s own control where the route renders one (so that control's own
    /// checked state stays consistent with what is on screen — auditing a "Light" chip marked
    /// current over a dark page would be a rendering the app never produces), and through
    /// <c>theme.ts</c>'s <c>setThemePreference</c> effect — the stored preference plus the
    /// <c>data-theme</c> attribute — on every other route, which is the same pair of writes that
    /// control performs.
    /// <para>
    /// The palette is CSS-token driven off <c>data-theme</c> (`index.css`), so a repaint is what a
    /// reload would produce; that the repaint actually happened is proved rather than assumed, by
    /// asserting <c>--background</c> resolves to a different value afterwards. A flip that silently
    /// did nothing would otherwise scan the light page twice and report it as the dark result.
    /// </para>
    /// </summary>
    private static async Task SwitchThemeAsync(IPage page, AuditedTheme theme)
    {
        string before = await ResolvedBackgroundTokenAsync(page);

        ILocator themeControl = page
            .GetByRole(AriaRole.Radiogroup, new() { Name = "Theme preference" })
            .GetByRole(AriaRole.Radio, new() { Name = theme == AuditedTheme.Dark ? "Dark" : "Light", Exact = true });
        if (await themeControl.CountAsync() > 0)
        {
            await themeControl.ClickAsync();
        }
        else
        {
            await page.EvaluateAsync(
                """
                value => {
                    try { localStorage.setItem('partyup:theme', value); } catch (e) {}
                    document.documentElement.setAttribute('data-theme', value);
                }
                """,
                AxeAuditWorld.ThemeValue(theme));
        }

        await AxeAuditWorld.AssertThemeAppliedAsync(page, theme);
        Assert.NotEqual(before, await ResolvedBackgroundTokenAsync(page));
    }

    private static async Task<string> ResolvedBackgroundTokenAsync(IPage page) =>
        await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.documentElement).getPropertyValue('--background').trim()");
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
    /// <summary>
    /// Phase 8 S7: <see cref="AuditedRoutes.ByName"/> and the theory's per-row filterability both
    /// depend on names being unique — a duplicate would silently collapse two routes into one
    /// audited row (or throw mid-theory) rather than failing here.
    /// </summary>
    [Fact]
    public void EveryAuditedRouteNameIsUnique_D139()
    {
        string[] duplicates = AuditedRoutes.All
            .GroupBy(route => route.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.True(
            duplicates.Length == 0,
            "Audited route names must be unique; duplicated: " + string.Join(", ", duplicates));
    }

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
