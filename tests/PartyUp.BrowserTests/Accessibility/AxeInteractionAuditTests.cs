using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Accessibility;

/// <summary>
/// The interaction-state half of the axe suite (PHASE8_DESIGN.md §3 S7): every state a viewer
/// reaches by <em>doing</em> something — an open combobox popup, a modal mid-flow, a live selection,
/// a focused control — none of which <see cref="AxeAuditTests"/>'s initial-render route theory can
/// reach, because the state is not addressable by URL.
/// <para>
/// Its own collection, so it runs beside the route theory rather than after it, on its own signed-in
/// context over the accounts <see cref="AxeAuditWorld"/> already built.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(AxeInteractionSuite.Name)]
public sealed class AxeInteractionAuditTests(PlaywrightFixture fixture)
{
    private static readonly SemaphoreSlim OrganizerLock = new(1, 1);

    private static Task<IBrowserContext>? _organizerTask;

    /// <summary>
    /// This collection's own signed-in organizer context — see <see cref="AxeAuditWorld"/> for why
    /// the accounts are shared across collections but the contexts are not.
    /// </summary>
    private async Task<IBrowserContext> GetOrganizerContextAsync()
    {
        if (_organizerTask is not null)
        {
            return await _organizerTask;
        }

        await OrganizerLock.WaitAsync();
        try
        {
            _organizerTask ??= SignInOrganizerAsync();
        }
        finally
        {
            OrganizerLock.Release();
        }

        return await _organizerTask;
    }

    private async Task<IBrowserContext> SignInOrganizerAsync()
    {
        AxeAuditWorld.AuditContext context = await AxeAuditWorld.GetContextAsync(fixture);
        return await fixture.SignInNewContextAsync(context.Organizer.Username);
    }

    /// <summary>
    /// TOUR_DESIGN.md §6: the tour's own axe coverage reaches the overlay through Replay — the
    /// fixture skip (`PlaywrightFixture.RegisterAndOnboardAsync`) makes auto-open unreachable for
    /// every lane account by design, including this class's own organizer, so this is the only path
    /// back to an open tour. Reused per theme: a fresh tab reaches `/settings/account` under a
    /// pre-navigation theme seed (`AxeAuditWorld.SeedThemeBeforeFirstNavigationAsync`, the same idiom the
    /// onboarding-step capture reload above uses), same as a real theme swap's own reload would —
    /// auditing the *already-closed* overlay from a stale page would silently pass.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task TourOverlay_ReopenedViaReplay_HasNoWcagViolationOutsideTheDatedExclusionList_D182(
        AuditedTheme theme)
    {
        IBrowserContext organizer = await GetOrganizerContextAsync();
        IPage page = await organizer.NewPageAsync();
        try
        {
            await AxeAuditWorld.SeedThemeBeforeFirstNavigationAsync(page, theme);
            await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/settings/account");
            await AxeAuditWorld.AssertThemeAppliedAsync(page, theme);

            await page.GetByRole(AriaRole.Button, new() { Name = "Replay the tour" }).ClickAsync();
            await page.GetByRole(AriaRole.Dialog).WaitForAsync();

            AxeResultItem[] violations = await AxeAuditWorld.RunAxeAsync(page);
            HashSet<string> excludedRuleIds = AxeExclusions.Entries
                .Where(entry => entry.RouteName == "tour-overlay")
                .Select(entry => entry.RuleId)
                .ToHashSet(StringComparer.Ordinal);
            AxeResultItem[] unexpected = violations
                .Where(violation => !excludedRuleIds.Contains(violation.Id))
                .ToArray();

            if (unexpected.Length > 0)
            {
                Directory.CreateDirectory(AxeAuditWorld.AxeArtifactsDirectory);
                string dumpPath = Path.Combine(
                    AxeAuditWorld.AxeArtifactsDirectory, $"tour-overlay.{AxeAuditWorld.ThemeValue(theme)}.json");
                await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(unexpected, AxeAuditWorld.DumpJsonOptions));
            }

            Assert.True(
                unexpected.Length == 0,
                $"Tour overlay ({AxeAuditWorld.ThemeValue(theme)}) has {unexpected.Length} un-excluded axe " +
                "violation(s): " + string.Join(", ", unexpected.Select(v => $"{v.Id} ({v.Impact})")) +
                $". Details in {AxeAuditWorld.AxeArtifactsDirectory}.");

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
        Func<IPage, Task> reachState,
        (int Width, int Height)? viewport = null)
    {
        IPage page = await browserContext.NewPageAsync();
        try
        {
            if (viewport is not null)
            {
                await page.SetViewportSizeAsync(viewport.Value.Width, viewport.Value.Height);
            }

            await AxeAuditWorld.SeedThemeBeforeFirstNavigationAsync(page, theme);
            await page.GotoAsync(PlaywrightFixture.WebBaseUrl + path);
            await AxeAuditWorld.AssertThemeAppliedAsync(page, theme);
            await reachState(page);

            AxeResultItem[] violations = await AxeAuditWorld.RunAxeAsync(page);
            HashSet<string> excludedRuleIds = AxeExclusions.Entries
                .Where(entry => entry.RouteName == stateName)
                .Select(entry => entry.RuleId)
                .ToHashSet(StringComparer.Ordinal);
            AxeResultItem[] unexpected = violations
                .Where(violation => !excludedRuleIds.Contains(violation.Id))
                .ToArray();

            if (unexpected.Length > 0)
            {
                Directory.CreateDirectory(AxeAuditWorld.AxeArtifactsDirectory);
                string dumpPath = Path.Combine(
                    AxeAuditWorld.AxeArtifactsDirectory, $"{stateName}.{AxeAuditWorld.ThemeValue(theme)}.json");
                await File.WriteAllTextAsync(
                    dumpPath, JsonSerializer.Serialize(unexpected, AxeAuditWorld.DumpJsonOptions));
            }

            Assert.True(
                unexpected.Length == 0,
                $"Interaction state '{stateName}' ({AxeAuditWorld.ThemeValue(theme)}) has {unexpected.Length} " +
                "un-excluded axe violation(s): " +
                string.Join(", ", unexpected.Select(v => $"{v.Id} ({v.Impact})")) +
                $". Details in {AxeAuditWorld.AxeArtifactsDirectory}.");
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
        IBrowserContext organizer = await GetOrganizerContextAsync();
        await fixture.SeedSteamCatalogAppsAsync(
            (570, "Dota 2"), (730, "Counter-Strike 2"), (440, "Team Fortress 2"));

        await AssertInteractionStateIsAxeCleanAsync(
            "steam-game-picker-open",
            theme,
            organizer,
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
    /// one walk: paint a range to raise the commit dialog, cancel it, then edit the seeded weekly
    /// series' edit — which raises the D-134/D-143 scope dialog — and finally its delete confirm.
    /// Nothing is saved, so the organizer's calendar is unchanged for the route cases.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task AvailabilityDialogs_HaveNoWcagViolation_D139(AuditedTheme theme)
    {
        IBrowserContext organizer = await GetOrganizerContextAsync();

        await AssertInteractionStateIsAxeCleanAsync(
            "availability-commit-dialog",
            theme,
            organizer,
            "/calendar",
            async page =>
            {
                await PlaywrightFixture.WaitForRequestQuiescenceAsync(page);
                await page.GetByRole(AriaRole.Gridcell).First.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await page.Keyboard.PressAsync("Shift+ArrowDown");
                await page.Keyboard.PressAsync("Enter");
                // Named by the painted range itself, so it is matched on the time-range shape
                // rather than on a fixed title string.
                await page.GetByRole(
                        AriaRole.Dialog,
                        new() { NameRegex = new Regex(@"\d{1,2}:\d{2}.*\d{1,2}:\d{2}") })
                    .WaitForAsync();
            });

        await AssertInteractionStateIsAxeCleanAsync(
            "availability-occurrence-scope-dialog",
            theme,
            organizer,
            "/calendar",
            async page =>
            {
                await PlaywrightFixture.WaitForRequestQuiescenceAsync(page);
                // The panel lists weekly rules first, so its first row is the seeded series
                // occurrence — the one case whose edit has to ask about scope.
                await page.GetByRole(AriaRole.Button, new() { Name = "Edit window" }).First.ClickAsync();
                await page.GetByLabel("Start time", new() { Exact = true })
                    .SelectOptionAsync(new SelectOptionValue { Value = "555" });
                await page.GetByRole(AriaRole.Dialog)
                    .GetByRole(AriaRole.Heading, new() { Name = "Which occurrences?" })
                    .WaitForAsync();
            });

        await AssertInteractionStateIsAxeCleanAsync(
            "availability-delete-series-confirm",
            theme,
            organizer,
            "/calendar",
            async page =>
            {
                await PlaywrightFixture.WaitForRequestQuiescenceAsync(page);
                // Deleting a weekly rule is the surface's other modal: an alertdialog that says
                // the removal reaches every future week before anything is written.
                await page.GetByRole(AriaRole.Button, new() { Name = "Delete window" }).First.ClickAsync();
                await page.GetByRole(AriaRole.Alertdialog)
                    .GetByRole(AriaRole.Heading, new() { Name = "Delete this weekly rule?" })
                    .WaitForAsync();
            });
    }

    /// <summary>
    /// A live keyboard selection on the week grid — the state between the first `Enter` and the one
    /// that commits, where the grid carries `aria-selected` cells, a band preview and a `role=status`
    /// extent announcement that no initial-render scan has ever seen.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task WeekGridMidDragSelection_HasNoWcagViolation_D139(AuditedTheme theme)
    {
        IBrowserContext organizer = await GetOrganizerContextAsync();

        await AssertInteractionStateIsAxeCleanAsync(
            "week-grid-mid-selection",
            theme,
            organizer,
            "/calendar",
            async page =>
            {
                await PlaywrightFixture.WaitForRequestQuiescenceAsync(page);
                await page.GetByRole(AriaRole.Gridcell).First.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await page.Keyboard.PressAsync("Shift+ArrowDown");
                await page.Keyboard.PressAsync("Shift+ArrowDown");
                // The selection is live and uncommitted: the cells it covers report
                // `aria-selected`, and no dialog has opened.
                await Assertions
                    .Expect(page.Locator("[role=gridcell][aria-selected=true]"))
                    .Not.ToHaveCountAsync(0);
                await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
            });
    }

    /// <summary>
    /// CALENDAR_DESIGN.md §9 S6: a 390×844 <c>/calendar</c> scan — the day strip, the sticky header
    /// S5 restored below <c>sm</c>, and the now-line dot only render at this width, so the desktop
    /// <c>calendar</c> route case in <see cref="Routes"/> never exercises them. Static page load
    /// only: the S3 bottom sheet needs a real drag gesture to open, which this fact does not
    /// perform. As of this writing that surface's own axe coverage is the
    /// <c>availability-commit-dialog</c>/<c>availability-occurrence-scope-dialog</c>/
    /// <c>availability-delete-series-confirm</c> facts above, which scan the same dialog's desktop
    /// popover rendering (S3 keeps identical dialog semantics between the popover and bottom-sheet
    /// arms; only the anchor position differs) — not the bottom sheet itself at this width.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task Calendar390pxDayStrip_HasNoWcagViolation_D139(AuditedTheme theme)
    {
        IBrowserContext organizer = await GetOrganizerContextAsync();

        await AssertInteractionStateIsAxeCleanAsync(
            "calendar-390-mobile",
            theme,
            organizer,
            "/calendar",
            async page => await PlaywrightFixture.WaitForRequestQuiescenceAsync(page),
            viewport: (390, 844));
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
        IBrowserContext organizer = await GetOrganizerContextAsync();
        AxeAuditWorld.AuditContext context = await AxeAuditWorld.GetContextAsync(fixture);

        await AssertInteractionStateIsAxeCleanAsync(
            "voting-grid-focused",
            theme,
            organizer,
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
        IBrowserContext organizer = await GetOrganizerContextAsync();

        await AssertInteractionStateIsAxeCleanAsync(
            "tour-overlay-mid-run",
            theme,
            organizer,
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
    /// D-139's push-enable prompt (D-147/D-159/D-175) — a `StatusBanner` reached only by an
    /// account whose `pushPromptEligible` fact is true and who has not yet been asked in this
    /// browser (`PushEnableCard.tsx`), never by initial-render alone. Mirrors
    /// <see cref="Notifications.PushEnableFlowTests"/>'s eligibility arrangement: a fresh account
    /// completes D-147's qualifying action (creating a private session, so no invitee is needed)
    /// then a real navigation to `/app` forces the profile query to re-fetch the now-qualified
    /// fact, which is what makes the card render at all.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task PushPromptStatusBanner_HasNoWcagViolation_D139(AuditedTheme theme)
    {
        RegisteredUser recipient = await fixture.SeedRegisteredUserAsync(
            await fixture.NewContextAsync(), "pushbanneraudit", "Push Banner Audit");

        await recipient.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");
        await recipient.Page.Locator("#field-title").FillAsync("Push banner axe session");
        await recipient.Page.Locator("#field-start").FillAsync(
            DateTime.UtcNow.AddDays(1).Date.AddHours(18).ToString(
                "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));
        await recipient.Page.Locator("#field-duration").FillAsync("60");
        await recipient.Page.Locator("#field-min").FillAsync("1");
        await recipient.Page.Locator("#field-max").FillAsync("4");
        await recipient.Page.GetByRole(AriaRole.Radiogroup, new() { Name = "Private session" })
            .GetByRole(AriaRole.Radio, new() { Name = "Private" }).ClickAsync();
        await recipient.Page.GetByRole(AriaRole.Button, new() { Name = "Create session" }).ClickAsync();
        await recipient.Page.WaitForURLAsync(url => Regex.IsMatch(url, @"/sessions/[0-9a-fA-F-]{36}$"));

        await AssertInteractionStateIsAxeCleanAsync(
            "status-banner-visible",
            theme,
            recipient.Page.Context,
            "/app",
            async page =>
            {
                await page.GetByTestId("push-enable-card").WaitForAsync();
            });
    }

    /// <summary>
    /// STYLING_OVERHAUL_DESIGN.md §3.3/§7 S2: a friend row's destructive actions live behind an
    /// ActionMenu, so the only state where those `menuitem`s exist at all is an opened menu — the
    /// `/friends` route scan sees a closed trigger and nothing else. Needs a real friendship (the
    /// row is the menu's host), which the shared audit organizer deliberately never has: its own
    /// friend code is read off a zero-friend `/friends`, so befriending it would change what the
    /// route cases scan. Two fresh accounts instead, mirroring the push-prompt fact above.
    /// </summary>
    [Theory]
    [InlineData(AuditedTheme.Light)]
    [InlineData(AuditedTheme.Dark)]
    public async Task FriendActionMenuOpen_HasNoWcagViolation_D139(AuditedTheme theme)
    {
        RegisteredUser viewer = await fixture.SeedRegisteredUserAsync(
            await fixture.NewContextAsync(), "friendmenuaudit", "Friend Menu Audit");
        RegisteredUser peer = await fixture.SeedRegisteredUserAsync(
            await fixture.NewContextAsync(), "friendmenupeer", "Friend Menu Peer");

        // `viewer` accepts, so `peer` is the row on `viewer`'s own friends list.
        await PlaywrightFixture.BefriendAsync(peer, viewer);

        await AssertInteractionStateIsAxeCleanAsync(
            "friend-actionmenu-open",
            theme,
            viewer.Page.Context,
            "/friends",
            async page =>
            {
                await page.GetByRole(
                    AriaRole.Button, new() { Name = $"More actions @{peer.Username}" }).ClickAsync();
                ILocator menu = page.GetByRole(AriaRole.Menu);
                await menu.WaitForAsync();
                await Assertions.Expect(menu.GetByRole(AriaRole.Menuitem).First).ToBeVisibleAsync();
            });
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

            AxeResultItem[] violations = await AxeAuditWorld.RunAxeAsync(page);

            Assert.Contains(violations, v => v.Id is "label" or "image-alt");
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
