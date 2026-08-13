using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using PartyUp.Application.Auth;

namespace PartyUp.BrowserTests.Accessibility;

/// <summary>
/// The theme dimension every <see cref="AuditedRoutes"/> entry is now scanned under (PHASE5_1_DESIGN.md
/// §4 slice 8) — <c>Light</c> and <c>Dark</c> are both seeded explicitly (never left to the browser's
/// own default color scheme) so neither audit run's result depends on an unstated ambient preference.
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

    /// <summary>
    /// F2 fix: the cache slot holds the in-flight <see cref="Task{TResult}"/> itself, assigned
    /// inside the lock BEFORE setup's async work runs — not the completed result assigned only on
    /// success. A prior version cached nothing until the very end, so any exception partway
    /// through (e.g. the SQL backdoor's now-removed `docker` CLI dependency, F1) left the slot
    /// null; every later `[Theory]` case then re-entered the lock and re-ran the WHOLE expensive,
    /// non-idempotent setup from scratch — including `RegisterAndOnboardAsync` with the same fixed
    /// "axeorganizer" username, which fails on the *second* attempt with a duplicate-account error
    /// that has nothing to do with the real failure. Caching the task means every caller — the
    /// first and every subsequent one — awaits the exact same attempt and observes the exact same
    /// (real) exception, once, instead of a cascade of increasingly unrelated ones.
    /// </summary>
    private static Task<AuditContext>? _contextTask;

    private sealed record AuditContext(
        RegisteredUser Organizer,
        RegisteredUser Admin,
        string AdminUserId,
        Guid TokenSubjectId,
        string TokenSubjectEmail,
        string SessionId,
        string GuestLinkUrl,
        string OwnFriendCode,
        string PollId,
        string PollGuestLinkUrl,
        string PollAwaitingSelectionId,
        IReadOnlyDictionary<(string Step, AuditedTheme Theme), AxeResultItem[]> OnboardingFindings);

    /// <summary>
    /// One real registered account, one private session and one guest link — enough live entity ids
    /// to resolve every dynamic-segment route in <see cref="AuditedRoutes"/>. Built once and shared
    /// across every axe fact the same way the flow-test classes share their setup.
    /// <para>
    /// One organizer plus one admin audit account (Phase 8 S3 — the `/admin*` routes render their
    /// D-177 fail-closed branch for anyone without the role, so they cannot share the organizer),
    /// and a <b>private</b> session (D-099 exempts private sessions from needing an invited friend).
    /// `{username}`/`{code}` routes below resolve against the organizer's own identity rather than a
    /// second party's; `{adminUserId}` resolves against the admin account's own id.
    /// </para>
    /// <para>
    /// The lane overrides `register-ip` to 100 (`PlaywrightFixture.cs`'s
    /// <c>RegisterPerIpPerHourOverride</c>), not production's 5 — the registrations this collection
    /// ships (two here, plus each flow class's own) leave well over ninety remaining, and
    /// <see cref="Tour.TourFlowTests"/>'s own registration fits comfortably inside it. Each one still
    /// pays the fixture's 65-second shared-rate-window barrier.
    /// </para>
    /// </summary>
    private async Task<AuditContext> GetContextAsync()
    {
        if (_contextTask is not null)
        {
            return await _contextTask;
        }

        await SetupLock.WaitAsync();
        try
        {
            // Re-check under the lock (the ordinary double-checked pattern), but assign the task
            // — not its eventual result — before releasing the lock and before awaiting it below,
            // so a concurrent caller that arrives while setup is still running joins this same
            // attempt rather than starting a second one.
            _contextTask ??= BuildContextAsync();
        }
        finally
        {
            SetupLock.Release();
        }

        return await _contextTask;
    }

    private async Task<AuditContext> BuildContextAsync()
    {
        Dictionary<(string Step, AuditedTheme Theme), AxeResultItem[]> onboardingFindings = [];
            RegisteredUser organizer = await fixture.RegisterAndOnboardAsync(
                await fixture.NewContextAsync(),
                "axeorganizer",
                "Axe Organizer",
                onOnboardingStep: async (step, page) =>
                {
                    // Both themes are captured off this one live pause in the wizard's own walk
                    // (see this class's remarks and PlaywrightFixture.RegisterAndOnboardAsync's
                    // onOnboardingStep parameter) rather than a second registration walk — the
                    // class-level remarks above document this collection's registration budget as
                    // already fully spent. Each capture re-seeds localStorage and reloads the exact
                    // same step URL, so it is self-correcting regardless of which theme the previous
                    // step's capture left behind.
                    onboardingFindings[(step, AuditedTheme.Light)] =
                        await CaptureThemedOnboardingStepAxeAsync(page, AuditedTheme.Light);
                    onboardingFindings[(step, AuditedTheme.Dark)] =
                        await CaptureThemedOnboardingStepAxeAsync(page, AuditedTheme.Dark);
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

            // Phase 6 S5 (PHASE6_DESIGN.md §5/§6, ticket-P6-S5): one live poll — organizer, two
            // options and a guest link, all minted through the same `/polls/new` create form
            // (`CreatePollRequest.guestLinkCount` mints the link at creation, so no separate
            // invite-page round trip is needed the way the session flow above needs one — polls has
            // no invite/guest-management page yet, that UI is S6). Private, mirroring the session
            // fixture's own private-poll choice (D-125 exempts a private poll from needing an
            // invited friend), on the organizer's already-open, already-authenticated context.
            string pollId = await CreatePollFixtureAsync(organizer.Page);

            // Phase 6 S6 (PHASE6_DESIGN.md §5, ticket-P6-S6): a poll-typed guest link, minted
            // through the organizer's guest-management panel this slice ships on poll-detail —
            // the only place a poll guest link's URL is ever shown (create-time only sets a
            // count, never surfaces the minted link itself). Resolves the `guest-poll` audit
            // route below.
            string pollGuestLinkUrl = await MintPollGuestLinkFixtureAsync(organizer.Page, pollId);

            // Phase 6 S7 (PHASE6_DESIGN.md §5/§6.1, ticket-P6-S7): a second poll, voted then
            // backdated past its deadline via `PlaywrightFixture.BackdatePollPastDeadlineAsync`
            // (the poll's fixed 6/12/24/48h deadline choice makes a real wait infeasible in this
            // lane) — reaches the awaitingSelection UI (organizer selection controls + finalization
            // preview) the S5 poll above never renders.
            string pollAwaitingSelectionId = await CreatePollFixtureAsync(organizer.Page, "Axe audit poll (awaiting selection)");
            ILocator firstVoteControl = organizer.Page.Locator("[data-testid^='vote-available-']").First;
            await firstVoteControl.ClickAsync();
            await organizer.Page.GetByText("Your vote was saved.").WaitForAsync();
            await fixture.BackdatePollPastDeadlineAsync(pollAwaitingSelectionId);

            // Phase 8 S3 (PHASE8_DESIGN.md §3 S3): a dedicated admin audit account — the three
            // `/admin*` routes render their D-177 fail-closed branch for anyone else, so scanning
            // them on the organizer's context would audit a one-paragraph not-found page. The role
            // is granted over the fixture's own connection (see `GrantAdminRoleAsync`); the reload
            // below is what makes the already-open context's cached queries refetch under it.
            RegisteredUser admin = await fixture.RegisterAndOnboardAsync(
                await fixture.NewContextAsync(),
                "axeadmin",
                // Never a display name whose tokens include "admin": `all.txt` holds it as an
                // exact token term, so `ProfanityFilter` rejects the draft outright.
                "Axe Operator");
            Guid adminUserId = await fixture.GrantAdminRoleAsync(admin.Username);
            await admin.Page.ReloadAsync();

            // Phase 8 S7 (S3 review finding): the `RequiresAdmin` cases below are only meaningful if
            // the grant above actually took. Both branches of `AdminDashboardPage` render the same
            // `screen-admin-dashboard` testid, and the D-177 fail-closed branch is a single
            // paragraph that trivially audits clean — so a silently failed role grant would turn
            // three admin route audits into three green scans of a not-found page. This sentinel
            // asserts real admin CONTENT (the dashboard heading) and the absence of the fail-closed
            // alert, on the same context those cases navigate, before any of them run.
            await admin.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/admin");
            await admin.Page.GetByRole(
                AriaRole.Heading, new() { Name = "Usage dashboard" }).WaitForAsync();
            Assert.Equal(
                0,
                await admin.Page.Locator("[data-testid=screen-admin-dashboard] [role=alert]")
                    .CountAsync());

            // Phase 8 S7 / Q4: the identity every minted token below is bound to. The organizer
            // backs them rather than a third registration — the `TokenForm` routes consume only on
            // submit and the audit never submits, so the account is left untouched.
            (Guid tokenSubjectId, string tokenSubjectEmail) =
                await fixture.GetAuthIdentityAsync(organizer.Username);

            return new AuditContext(
                organizer,
                admin,
                adminUserId.ToString(),
                tokenSubjectId,
                tokenSubjectEmail,
                sessionId,
                guestLinkUrl,
                ownFriendCode,
                pollId,
                pollGuestLinkUrl,
                pollAwaitingSelectionId,
                onboardingFindings);
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

    /// <summary>
    /// Drives the real `/polls/new` create form (PollCreatePage.tsx) to mint the live poll the
    /// `polls`/`poll-detail` audit routes below resolve `{pollId}` against — two candidate times
    /// (D-131's minimum), a guest link (D-125's private-guest-only arm, `guestLinkCount: 1`) and
    /// no invited friend, mirroring the session fixture's own private-session choice above.
    /// </summary>
    private static async Task<string> CreatePollFixtureAsync(IPage page, string title = "Axe audit poll")
    {
        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/new");
        await page.Locator("#field-title").FillAsync(title);

        string optionOne = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        string optionTwo = DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        await page.Locator("#field-option-value").FillAsync(optionOne);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add time" }).ClickAsync();
        await page.Locator("#field-option-value").FillAsync(optionTwo);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add time" }).ClickAsync();

        await page.Locator("#field-min").FillAsync("1");
        await page.Locator("#field-max").FillAsync("4");
        await page.GetByLabel("Private poll").CheckAsync();
        await page.Locator("#field-guest-links").FillAsync("1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create poll" }).ClickAsync();
        await page.WaitForURLAsync(url => Regex.IsMatch(url, @"/polls/[0-9a-fA-F-]{36}$"));
        return new Uri(page.Url).Segments[^1];
    }

    /// <summary>
    /// Drives the real organizer guest-management panel on poll-detail (`PollDetailPage.tsx`,
    /// ticket-P6-S6) to mint one poll-typed guest link and read its plaintext-shown-once URL.
    /// </summary>
    private static async Task<string> MintPollGuestLinkFixtureAsync(IPage page, string pollId)
    {
        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/" + pollId);
        await page.Locator("#poll-guest-link-count").FillAsync("1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create link" }).ClickAsync();
        return await page.Locator("span.font-mono").First.InnerTextAsync();
    }

    private static async Task<AxeResultItem[]> RunAxeAsync(IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        AxeResult result = await page.RunAxe(new AxeRunOptions { RunOnly = RunOnlyOptions.Tags(WcagTags) });
        return result.Violations;
    }

    private const string ThemeStorageKey = "partyup:theme";

    private static string ThemeValue(AuditedTheme theme) => theme == AuditedTheme.Dark ? "dark" : "light";

    /// <summary>
    /// Seeds <c>partyup:theme</c> before the page's very first navigation via
    /// <see cref="IPage.AddInitScriptAsync"/> — this only works on a page that has not yet loaded
    /// any document (a freshly created page/tab), which is exactly what both call sites below pass
    /// in. Explicit for both themes (never relies on the browser's own default color scheme) so a
    /// broken seed cannot silently audit light twice.
    /// </summary>
    private static async Task SeedThemeBeforeFirstNavigationAsync(IPage page, AuditedTheme theme)
    {
        await page.AddInitScriptAsync(
            $"try {{ localStorage.setItem('{ThemeStorageKey}', '{ThemeValue(theme)}'); }} catch (e) {{}}");
    }

    /// <summary>
    /// Fails loudly (rather than silently auditing the wrong theme) if the seed above did not take:
    /// reads the <c>data-theme</c> attribute <c>index.html</c>'s own inline script sets before first
    /// paint and asserts it matches.
    /// </summary>
    private static async Task AssertThemeAppliedAsync(IPage page, AuditedTheme theme)
    {
        string? actual = await page.EvaluateAsync<string?>(
            "document.documentElement.getAttribute('data-theme')");
        Assert.Equal(ThemeValue(theme), actual);
    }

    /// <summary>
    /// The onboarding-route half of the theme dimension: no fresh navigation is available (see this
    /// class's remarks), so this re-seeds <c>partyup:theme</c> on the wizard's own live page and
    /// reloads the exact same step URL — <c>index.html</c>'s inline script reads localStorage again
    /// before that reload's first paint, same as a fresh <c>GotoAsync</c> would.
    /// </summary>
    private static async Task<AxeResultItem[]> CaptureThemedOnboardingStepAxeAsync(IPage page, AuditedTheme theme)
    {
        await page.EvaluateAsync(
            "value => { try { localStorage.setItem('partyup:theme', value); } catch (e) {} }",
            ThemeValue(theme));
        await page.ReloadAsync();
        await AssertThemeAppliedAsync(page, theme);
        return await RunAxeAsync(page);
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
        AuditContext context = await GetContextAsync();

        AxeResultItem[] violations;
        if (route.OnboardingStepKey is not null)
        {
            // Captured live during GetContextAsync's registration walk — see this class's own
            // remarks and PlaywrightFixture.RegisterAndOnboardAsync's onOnboardingStep parameter.
            violations = context.OnboardingFindings[(route.OnboardingStepKey, theme)];
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
                await SeedThemeBeforeFirstNavigationAsync(page, theme);
                string guestLinkUrl = route.GuestLinkKind == GuestLinkKind.Poll
                    ? context.PollGuestLinkUrl
                    : context.GuestLinkUrl;
                await page.GotoAsync(guestLinkUrl);
            }
            else if (route.SignedOut)
            {
                // Phase 8 S7 / Q4: a mailed link opens in whatever browser the recipient has, signed
                // out — so these are audited in their own throwaway context, which also keeps a
                // single-use token from ever being reused across the light/dark pair (each case
                // mints its own below). Closed in the finally, like the guest contexts above.
                guestContext = await fixture.NewContextAsync();
                page = await guestContext.NewPageAsync();
                await SeedThemeBeforeFirstNavigationAsync(page, theme);

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
                // page's first paint. Closed in the finally below so per-theme runs don't accumulate
                // open tabs on that context across the route loop.
                page = route.RequiresAdmin
                    ? await context.Admin.Page.Context.NewPageAsync()
                    : await context.Organizer.Page.Context.NewPageAsync();
                await SeedThemeBeforeFirstNavigationAsync(page, theme);
                await page.GotoAsync(PlaywrightFixture.WebBaseUrl + path);
            }

            try
            {
                await AssertThemeAppliedAsync(page, theme);
                violations = await RunAxeAsync(page);
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
            string dumpPath = Path.Combine(AxeArtifactsDirectory, $"{route.Name}.{ThemeValue(theme)}.json");
            await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(unexpected, DumpJsonOptions));
        }

        Assert.True(
            unexpected.Length == 0,
            $"Route '{route.Name}' ({ThemeValue(theme)}) has {unexpected.Length} un-excluded axe " +
            "violation(s): " + string.Join(", ", unexpected.Select(v => $"{v.Id} ({v.Impact})")) +
            $". Details in {AxeArtifactsDirectory}.");
    }

    /// <summary>
    /// TOUR_DESIGN.md §6: the tour's own axe coverage reaches the overlay through Replay — the
    /// fixture skip (`PlaywrightFixture.RegisterAndOnboardAsync`) makes auto-open unreachable for
    /// every lane account by design, including this class's own organizer, so this is the only path
    /// back to an open tour. Reused per theme: a fresh tab reaches `/settings/account` under a
    /// pre-navigation theme seed (`SeedThemeBeforeFirstNavigationAsync`, the same idiom the
    /// onboarding-step capture reload above uses), same as a real theme swap's own reload would —
    /// auditing the *already-closed* overlay from a stale page would silently pass.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task TourOverlay_ReopenedViaReplay_HasNoWcagViolationOutsideTheDatedExclusionList_D182(
        AuditedTheme theme)
    {
        AuditContext context = await GetContextAsync();
        IPage page = await context.Organizer.Page.Context.NewPageAsync();
        try
        {
            await SeedThemeBeforeFirstNavigationAsync(page, theme);
            await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/settings/account");
            await AssertThemeAppliedAsync(page, theme);

            await page.GetByRole(AriaRole.Button, new() { Name = "Replay the tour" }).ClickAsync();
            await page.GetByRole(AriaRole.Dialog).WaitForAsync();

            AxeResultItem[] violations = await RunAxeAsync(page);
            HashSet<string> excludedRuleIds = AxeExclusions.Entries
                .Where(entry => entry.RouteName == "tour-overlay")
                .Select(entry => entry.RuleId)
                .ToHashSet(StringComparer.Ordinal);
            AxeResultItem[] unexpected = violations
                .Where(violation => !excludedRuleIds.Contains(violation.Id))
                .ToArray();

            if (unexpected.Length > 0)
            {
                Directory.CreateDirectory(AxeArtifactsDirectory);
                string dumpPath = Path.Combine(
                    AxeArtifactsDirectory, $"tour-overlay.{ThemeValue(theme)}.json");
                await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(unexpected, DumpJsonOptions));
            }

            Assert.True(
                unexpected.Length == 0,
                $"Tour overlay ({ThemeValue(theme)}) has {unexpected.Length} un-excluded axe " +
                "violation(s): " + string.Join(", ", unexpected.Select(v => $"{v.Id} ({v.Impact})")) +
                $". Details in {AxeArtifactsDirectory}.");

            // Dismiss so a leaked open tour never reaches a later test sharing this organizer.
            IResponse response = await page.RunAndWaitForResponseAsync(
                () => page.GetByRole(AriaRole.Button, new() { Name = "Skip tour" }).ClickAsync(),
                candidate => candidate.Url.Contains("/api/v1/me/tour-completed", StringComparison.Ordinal) &&
                    candidate.Request.Method == "POST");
            Assert.Equal(204, response.Status);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// PHASE8_DESIGN.md §3 S7's interaction-state half. The route theory above scans initial render
    /// only, so every state a viewer reaches by *doing* something — an open combobox popup, a modal
    /// mid-flow, a live selection, a focused control — has never been audited. These are scripted
    /// state-then-scan facts rather than <see cref="AuditedRoutes"/> entries: the state is not
    /// addressable by URL, so there is nothing for that theory to navigate to.
    /// <para>
    /// Each runs on a fresh tab of an already-authenticated context (never the account's persistent
    /// page) under a pre-navigation theme seed, the same idiom the route cases use, and closes it in
    /// a finally so the state loop doesn't accumulate tabs.
    /// </para>
    /// </summary>
    private static async Task AssertInteractionStateIsAxeCleanAsync(
        string stateName,
        AuditedTheme theme,
        IBrowserContext browserContext,
        string path,
        Func<IPage, Task> reachState)
    {
        IPage page = await browserContext.NewPageAsync();
        try
        {
            await SeedThemeBeforeFirstNavigationAsync(page, theme);
            await page.GotoAsync(PlaywrightFixture.WebBaseUrl + path);
            await AssertThemeAppliedAsync(page, theme);
            await reachState(page);

            AxeResultItem[] violations = await RunAxeAsync(page);
            HashSet<string> excludedRuleIds = AxeExclusions.Entries
                .Where(entry => entry.RouteName == stateName)
                .Select(entry => entry.RuleId)
                .ToHashSet(StringComparer.Ordinal);
            AxeResultItem[] unexpected = violations
                .Where(violation => !excludedRuleIds.Contains(violation.Id))
                .ToArray();

            if (unexpected.Length > 0)
            {
                Directory.CreateDirectory(AxeArtifactsDirectory);
                string dumpPath = Path.Combine(
                    AxeArtifactsDirectory, $"{stateName}.{ThemeValue(theme)}.json");
                await File.WriteAllTextAsync(
                    dumpPath, JsonSerializer.Serialize(unexpected, DumpJsonOptions));
            }

            Assert.True(
                unexpected.Length == 0,
                $"Interaction state '{stateName}' ({ThemeValue(theme)}) has {unexpected.Length} " +
                "un-excluded axe violation(s): " +
                string.Join(", ", unexpected.Select(v => $"{v.Id} ({v.Impact})")) +
                $". Details in {AxeArtifactsDirectory}.");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// D-194's game picker with its combobox popup open over real results. The catalog is populated
    /// by a Hangfire job against the live Steam API, which this lane must not depend on, so the rows
    /// are seeded over the fixture's own connection — without them the component takes its sticky
    /// D-058 fallback branch and there is no popup to audit at all, which this asserts before
    /// scanning rather than silently auditing the fallback.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task SteamGamePickerOpenWithResults_HasNoWcagViolation_D139(AuditedTheme theme)
    {
        AuditContext context = await GetContextAsync();
        await fixture.SeedSteamCatalogAppsAsync(
            (570, "Dota 2"), (730, "Counter-Strike 2"), (440, "Team Fortress 2"));

        await AssertInteractionStateIsAxeCleanAsync(
            "steam-game-picker-open",
            theme,
            context.Organizer.Page.Context,
            "/sessions/new",
            async page =>
            {
                await page.Locator("#field-platform").SelectOptionAsync("Steam");
                ILocator combobox = page.Locator("#field-game-name");
                await combobox.FillAsync("Team");
                ILocator listbox = page.GetByRole(AriaRole.Listbox);
                await listbox.WaitForAsync();
                await Assertions.Expect(listbox.GetByRole(AriaRole.Option).First).ToBeVisibleAsync();
            });
    }

    /// <summary>
    /// The three availability modals, reached the way a keyboard viewer reaches them and audited in
    /// one walk: paint a range to raise the commit dialog, cancel it, then open the seeded weekly
    /// series' busy cell — which raises the D-134/D-143 scope dialog, whose Confirm raises the block
    /// editor. Nothing is saved, so the organizer's calendar is unchanged for the route cases.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task AvailabilityDialogs_HaveNoWcagViolation_D139(AuditedTheme theme)
    {
        AuditContext context = await GetContextAsync();

        await AssertInteractionStateIsAxeCleanAsync(
            "availability-commit-dialog",
            theme,
            context.Organizer.Page.Context,
            "/calendar",
            async page =>
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.GetByRole(AriaRole.Gridcell).First.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await page.Keyboard.PressAsync("Shift+ArrowDown");
                await page.Keyboard.PressAsync("Enter");
                await page.GetByRole(AriaRole.Dialog)
                    .GetByText("Add this availability?").WaitForAsync();
            });

        await AssertInteractionStateIsAxeCleanAsync(
            "availability-occurrence-scope-dialog",
            theme,
            context.Organizer.Page.Context,
            "/calendar",
            async page =>
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                ILocator busyCell = page
                    .GetByRole(AriaRole.Gridcell, new() { NameRegex = new Regex("Busy") }).First;
                await busyCell.WaitForAsync();
                await busyCell.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await page.GetByRole(AriaRole.Dialog)
                    .GetByRole(AriaRole.Heading, new() { Name = "Which occurrences?" })
                    .WaitForAsync();
            });

        await AssertInteractionStateIsAxeCleanAsync(
            "availability-block-editor-dialog",
            theme,
            context.Organizer.Page.Context,
            "/calendar",
            async page =>
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                ILocator busyCell = page
                    .GetByRole(AriaRole.Gridcell, new() { NameRegex = new Regex("Busy") }).First;
                await busyCell.WaitForAsync();
                await busyCell.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await page.GetByRole(AriaRole.Button, new() { Name = "Confirm" }).ClickAsync();
                await page.GetByRole(AriaRole.Dialog)
                    .GetByText("Edit availability").WaitForAsync();
            });
    }

    /// <summary>
    /// A live keyboard selection on the week grid — the state between the first `Enter` and the one
    /// that commits, where the grid carries `data-selection-edge` band borders and a `role=status`
    /// extent announcement that no initial-render scan has ever seen.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task WeekGridMidDragSelection_HasNoWcagViolation_D139(AuditedTheme theme)
    {
        AuditContext context = await GetContextAsync();

        await AssertInteractionStateIsAxeCleanAsync(
            "week-grid-mid-selection",
            theme,
            context.Organizer.Page.Context,
            "/calendar",
            async page =>
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.GetByRole(AriaRole.Gridcell).First.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await page.Keyboard.PressAsync("Shift+ArrowDown");
                await page.Keyboard.PressAsync("Shift+ArrowDown");
                // The selection is live and uncommitted: banded cells, no dialog.
                await Assertions
                    .Expect(page.Locator("[role=gridcell][data-selection-edge]").First)
                    .ToBeVisibleAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
            });
    }

    /// <summary>
    /// The voting grid with one of its APG radiogroup controls focused — the focus ring, the roving
    /// tabindex and the checked fill are all state an unfocused initial-render scan misses.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task VotingGridFocusedControl_HasNoWcagViolation_D139(AuditedTheme theme)
    {
        AuditContext context = await GetContextAsync();

        await AssertInteractionStateIsAxeCleanAsync(
            "voting-grid-focused",
            theme,
            context.Organizer.Page.Context,
            "/polls/" + context.PollId,
            async page =>
            {
                ILocator control = page.Locator("[data-testid^='vote-available-']").First;
                await control.WaitForAsync();
                await control.FocusAsync();
                await Assertions.Expect(control).ToBeFocusedAsync();
            });
    }

    /// <summary>
    /// The tour overlay <b>mid-run</b>, not at its first step: the existing D-182 fact scans step 1,
    /// where the overlay has no "back" affordance and its progress text reads differently. This
    /// advances one step first, so the middle-of-the-run chrome is audited too.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task TourOverlayMidRun_HasNoWcagViolation_D139_D182(AuditedTheme theme)
    {
        AuditContext context = await GetContextAsync();

        await AssertInteractionStateIsAxeCleanAsync(
            "tour-overlay-mid-run",
            theme,
            context.Organizer.Page.Context,
            "/settings/account",
            async page =>
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Replay the tour" }).ClickAsync();
                ILocator dialog = page.GetByRole(AriaRole.Dialog);
                await dialog.WaitForAsync();
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
                // Step 2 of N — the dialog's own title carries the step number, so waiting on a
                // title that is no longer "Step 1" is what proves the advance actually happened.
                await Assertions
                    .Expect(dialog.GetByText(new Regex(@"Step 2 of \d+")))
                    .ToBeVisibleAsync();
            });

        // The replayed tour is left open on a tab this helper already closed; the account's
        // `tourCompleted` flag is untouched by Replay, so nothing leaks into a later fact.
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
