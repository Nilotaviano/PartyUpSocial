using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Accessibility;

/// <summary>
/// The one audit world both axe collections read: the shared organizer and admin accounts, the live
/// session, poll and guest links every <see cref="AuditedRoutes"/> entry resolves against, the
/// onboarding-step findings captured during the organizer's own registration walk, and the axe and
/// theme primitives every scan is built from.
/// <para>
/// It is built exactly once per run (the cached in-flight <see cref="Task{TResult}"/> idiom below)
/// no matter how many collections ask for it — the walk is expensive and non-idempotent, and a
/// second one would collide on the same fixed usernames. The browser contexts the scans actually run
/// on are deliberately NOT shared: each collection signs into its own
/// (<see cref="PlaywrightFixture.SignInNewContextAsync"/>), because a context is the unit of
/// <c>localStorage</c> isolation and the theme dimension is seeded there.
/// </para>
/// </summary>
internal static class AxeAuditWorld
{
    private static readonly string[] WcagTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
    internal static readonly JsonSerializerOptions DumpJsonOptions = new() { WriteIndented = true };
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

    internal sealed record AuditContext(
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
    /// The organizer is the one account in this class that must still be built by driving the real
    /// registration UI — the <c>/onboarding*</c> routes are audited from inside that walk (see this
    /// class's remarks above), so there is nothing to scan without it. It pays the multi-page
    /// mailbox walk and one `register-ip` slot of the lane's overridden 100
    /// (`PlaywrightFixture.cs`'s <c>RegisterPerIpPerHourOverride</c>, not production's 5). The admin
    /// account asserts nothing about registration, so it is seeded in-process instead
    /// (<see cref="PlaywrightFixture.SeedRegisteredUserAsync"/>) and pays neither.
    /// </para>
    /// </summary>
    internal static async Task<AuditContext> GetContextAsync(PlaywrightFixture fixture)
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
            _contextTask ??= BuildContextAsync(fixture);
        }
        finally
        {
            SetupLock.Release();
        }

        return await _contextTask;
    }

    private static async Task<AuditContext> BuildContextAsync(PlaywrightFixture fixture)
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
            RegisteredUser admin = await fixture.SeedRegisteredUserAsync(
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

        // Two candidate times off the click-to-toggle `CandidateGrid`
        // (`data-testid="candidate-slot-{dayIndex}-{minuteOfDay}"`), replacing the old
        // `#field-option-value` + "Add time" date-input flow. "Next week" keeps both slots
        // unambiguously in the future.
        await page.GetByRole(AriaRole.Button, new() { Name = "Next week" }).ClickAsync();
        await page.GetByTestId("candidate-slot-1-1020").ClickAsync();
        await page.GetByTestId("candidate-slot-2-1020").ClickAsync();

        await page.Locator("#field-min").FillAsync("1");
        await page.Locator("#field-max").FillAsync("4");
        await page.GetByRole(AriaRole.Radiogroup, new() { Name = "Private poll" })
            .GetByRole(AriaRole.Radio, new() { Name = "Private" }).ClickAsync();
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

    internal static async Task<AxeResultItem[]> RunAxeAsync(IPage page)
    {
        await PlaywrightFixture.WaitForRequestQuiescenceAsync(page);
        await WaitForAnimationsToSettleAsync(page);
        AxeResult result = await page.RunAxe(new AxeRunOptions { RunOnly = RunOnlyOptions.Tags(WcagTags) });
        return result.Violations;
    }

    /// <summary>
    /// Contrast is a property of the settled surface. Dialogs enter behind a short fade, and axe
    /// composites an element's colours through every ancestor opacity — so a scan that lands
    /// mid-fade measures a partially transparent blend nobody ever reads, and reports a ratio the
    /// finished UI does not have. Endlessly repeating animations (the now-line pulse) never reach
    /// a finished state, so they are excluded rather than waited on; a settle that does not arrive
    /// inside the budget leaves the scan exactly where it would have been without this wait.
    /// </summary>
    private static async Task WaitForAnimationsToSettleAsync(IPage page)
    {
        try
        {
            await page.WaitForFunctionAsync(
                """
                () => document.getAnimations().every(animation =>
                    animation.playState !== 'running'
                    || animation.effect?.getComputedTiming?.().iterations === Infinity)
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
        }
    }

    private const string ThemeStorageKey = "partyup:theme";

    internal static string ThemeValue(AuditedTheme theme) => theme == AuditedTheme.Dark ? "dark" : "light";

    /// <summary>
    /// Seeds <c>partyup:theme</c> before the page's very first navigation via
    /// <see cref="IPage.AddInitScriptAsync"/> — this only works on a page that has not yet loaded
    /// any document (a freshly created page/tab), which is exactly what both call sites below pass
    /// in. Explicit for both themes (never relies on the browser's own default color scheme) so a
    /// broken seed cannot silently audit light twice.
    /// </summary>
    internal static async Task SeedThemeBeforeFirstNavigationAsync(IPage page, AuditedTheme theme)
    {
        await page.AddInitScriptAsync(
            $"try {{ localStorage.setItem('{ThemeStorageKey}', '{ThemeValue(theme)}'); }} catch (e) {{}}");
    }

    /// <summary>
    /// Fails loudly (rather than silently auditing the wrong theme) if the seed above did not take:
    /// reads the <c>data-theme</c> attribute <c>index.html</c>'s own inline script sets before first
    /// paint and asserts it matches.
    /// </summary>
    internal static async Task AssertThemeAppliedAsync(IPage page, AuditedTheme theme)
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

    internal static string AxeArtifactsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "axe-results");
}
