using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §11 "Browser — Chromium (S6/S8)": real-browser, keyboard-only proof of the week
/// grid's core loop (create/edit/delete a one-time block, P5-CAL-01) and the D-134/D-143 occurrence
/// scope choice, reachable without a pointer.
/// <para>
/// Round-1 F9's documented constraint applies here: <c>register-ip</c> is 5/hour/IP
/// (`RateLimitPolicies.cs`) and the Chromium step's other classes in this collection
/// (<c>SessionRegisteredFlowTests</c> 3, <c>SessionGuestFlowTests</c> 1, <c>AxeAuditTests</c> 1)
/// already spend the whole budget, so this class registers exactly one account, shared (lazily,
/// once) across the facts below — never a second.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class AvailabilityCalendarFlowTests(PlaywrightFixture fixture)
{
    /// <summary>
    /// The commit dialog's accessible name is the painted range, formatted in the viewer's locale
    /// — matched on the "time separator time" shape so it survives any range value.
    /// </summary>
    private static readonly Regex CommitRangePattern = new(@"\d{1,2}:\d{2}.*\d{1,2}:\d{2}");

    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static RegisteredUser? _organizer;

    private async Task<RegisteredUser> GetOrganizerAsync()
    {
        if (_organizer is not null)
        {
            return _organizer;
        }

        await SetupLock.WaitAsync();
        try
        {
            _organizer ??= await fixture.SeedRegisteredUserAsync(
                await fixture.NewContextAsync(), "calendarorganizer", "Calendar Organizer");
            return _organizer;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    /// <summary>
    /// No web UI authors a recurring series (S6 ships no such control — availability series
    /// creation stays server-side-only in Phase 5's write set); the scope dialog this test proves
    /// reachable needs one to exist, so it is seeded through the same authenticated origin the SPA
    /// itself calls, from inside the page (shares the session cookie; mints its own antiforgery
    /// token exactly the way `web/src/api/client.ts` does), never a second HTTP client or a second
    /// registration.
    /// </summary>
    private static async Task SeedWeeklySeriesAsync(IPage page)
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
                        // "HH:mm" — the wire's LocalTimePattern is strict, no seconds
                        // (AvailabilityWireFormat.cs's TimePattern).
                        startTime: '10:00',
                        durationMinutes: 60,
                        effectiveFrom,
                        effectiveUntil: null,
                    }),
                });
                if (!response.ok) {
                    throw new Error('Series seed failed: ' + response.status);
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
    /// Geometry, not semantics: the functional and axe suites passed while the shipped grid
    /// rendered as one vertical column of cells (its rows carried no layout CSS), so at least one
    /// browser assertion must pin the two-dimensional shape itself — cells of one row share a top
    /// edge with distinct lefts, cells of one column share a left edge with distinct tops.
    /// </summary>
    [Fact]
    public async Task GridCellsLayOutInRowsAndColumns_D132()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        IPage page = organizer.Page;

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/calendar");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        ILocator grid = page.GetByRole(AriaRole.Grid);
        ILocator firstRowCells = grid.Locator("[role=row]").Nth(1).Locator("[role=gridcell]");
        ILocator secondRowCells = grid.Locator("[role=row]").Nth(2).Locator("[role=gridcell]");
        Assert.True(await firstRowCells.CountAsync() >= 2, "expected at least two day cells per row");

        LocatorBoundingBoxResult? a = await firstRowCells.Nth(0).BoundingBoxAsync();
        LocatorBoundingBoxResult? b = await firstRowCells.Nth(1).BoundingBoxAsync();
        LocatorBoundingBoxResult? below = await secondRowCells.Nth(0).BoundingBoxAsync();
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(below);

        Assert.True(Math.Abs(a!.Y - b!.Y) < 1, $"same-row cells must share a top edge (got {a.Y} vs {b.Y})");
        Assert.True(b.X > a.X + 1, $"same-row cells must occupy distinct columns (got {a.X} vs {b.X})");
        Assert.True(Math.Abs(a.X - below!.X) < 1, $"same-column cells must share a left edge (got {a.X} vs {below.X})");
        Assert.True(below.Y > a.Y + 1, $"same-column cells must occupy distinct rows (got {a.Y} vs {below.Y})");
    }

    /// <summary>
    /// Phase 5.1 S1 geometry gate (PHASE5_1_DESIGN.md §3 "Grid geometry gate"): the row-ordering
    /// fact above passes at any height, so it is not a tripwire against a font or layout change
    /// collapsing the calendar's real touch targets. WCAG 2.5.8 sizes *pointer* targets, and the
    /// 15-minute cell layer is not one — it is a transparent <c>pointer-events:none</c> overlay
    /// that exists for the keyboard and the accessibility tree only (`lovable/WeekGrid.tsx`). So
    /// this pins the two things that are real: the painted day columns a finger actually drags on
    /// stay full-height and wide enough to hit, and every control on the calendar screen clears
    /// the 24px target-size floor. Row uniformity is checked without a pixel-pinned baseline,
    /// since fonts legitimately move metrics slightly.
    /// </summary>
    [Fact]
    public async Task GridCellsMeetGeometryFloor_D132()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        IPage page = organizer.Page;

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/calendar");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        ILocator rows = page.GetByRole(AriaRole.Grid).Locator("[role=row]");
        int rowCount = await rows.CountAsync();
        Assert.True(rowCount >= 3, "expected several time-slot rows in the visible week");

        double? firstRowHeight = null;
        for (int rowIndex = 1; rowIndex < rowCount; rowIndex++)
        {
            ILocator cells = rows.Nth(rowIndex).Locator("[role=gridcell]");
            int cellCount = await cells.CountAsync();
            Assert.True(cellCount >= 1, $"row {rowIndex} has no gridcells");

            LocatorBoundingBoxResult? box = await cells.First.BoundingBoxAsync();
            Assert.NotNull(box);
            Assert.True(box!.Height > 0, $"row {rowIndex} has zero-height cells");

            firstRowHeight ??= box.Height;
            Assert.True(
                Math.Abs(box.Height - firstRowHeight.Value) < 1,
                $"row {rowIndex} height {box.Height} differs from row 1's {firstRowHeight}");
        }

        // The paint surface itself: one column per shown day, each tall enough to hold every row
        // and wide enough to hit with a finger.
        ILocator columns = page.Locator("[data-daycol]");
        int columnCount = await columns.CountAsync();
        Assert.True(columnCount >= 1, "expected at least one painted day column");
        double gridHeight = firstRowHeight!.Value * (rowCount - 1);
        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            LocatorBoundingBoxResult? box = await columns.Nth(columnIndex).BoundingBoxAsync();
            Assert.NotNull(box);
            Assert.True(box!.Width >= 44, $"day column {columnIndex} is {box.Width}px wide, expected >= 44px");
            Assert.True(
                box.Height >= gridHeight - 1,
                $"day column {columnIndex} is {box.Height}px tall, expected to span the {gridHeight}px grid");
        }

        // WCAG 2.2 AA 2.5.8: every control on the calendar clears 24×24 CSS px.
        ILocator controls = page.Locator("[data-testid=screen-calendar] button:visible");
        int controlCount = await controls.CountAsync();
        Assert.True(controlCount >= 1, "expected the calendar screen to carry controls");
        for (int controlIndex = 0; controlIndex < controlCount; controlIndex++)
        {
            LocatorBoundingBoxResult? box = await controls.Nth(controlIndex).BoundingBoxAsync();
            if (box is null)
            {
                continue;
            }

            Assert.True(
                box.Width >= 24 && box.Height >= 24,
                $"control {controlIndex} is {box.Width}×{box.Height}px, below the 24×24 target-size floor");
        }
    }

    [Fact]
    public async Task KeyboardOnlyCreateEditDelete_D132()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        IPage page = organizer.Page;

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/calendar");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Counted relative to a baseline, not an absolute 0/1: the sibling fact in this class
        // shares this account and seeds a recurring series that can legitimately show its own
        // busy cell in the same visible week (xunit does not order facts across a class).
        // Busy state is the server's own merge, read straight off the cell layer (A-076).
        ILocator busyCells = page.Locator("[role=gridcell][data-state=busy]");
        int baselineBusyCount = await busyCells.CountAsync();

        // Create: focus the grid's one tab stop (08:00 on the week's first day, the clamped
        // viewport's first row — always earlier than the sibling fact's fixed 10:00 series, so
        // it sorts first in DOM order below regardless of run order), then keyboard alone —
        // Enter starts a selection, Shift+ArrowDown extends it by one 15-minute slot, Enter ends
        // the paint (P5-CAL-01). This first dialog is Escaped away to prove the trap and focus
        // return; the block actually saved is the repaint below.
        ILocator firstCell = page.GetByRole(AriaRole.Gridcell).First;
        await firstCell.FocusAsync();
        await Assertions.Expect(firstCell).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.PressAsync("Shift+ArrowDown");
        await page.Keyboard.PressAsync("Enter");

        // D-009/§7: the painted range is only a proposal — nothing is created until the commit
        // dialog's one-time/weekly choice is confirmed. Keyboard-only, so the choice is left at
        // its "One-time" default (a one-time block, which the edit half below depends on) and
        // Save is reached and activated without a pointer. The dialog is named by the painted
        // range itself, so it is matched on the time-range shape rather than a fixed title.
        ILocator commitDialog = page.GetByRole(AriaRole.Dialog, new() { NameRegex = CommitRangePattern });
        await commitDialog.WaitForAsync();
        await Assertions.Expect(busyCells).ToHaveCountAsync(baselineBusyCount);
        await Assertions
            .Expect(commitDialog.GetByRole(AriaRole.Button, new() { Name = "One-time", Exact = true }))
            .ToHaveAttributeAsync("aria-pressed", "true");

        // WCAG 2.4.3/2.1.2: the popover declares `aria-modal`, so Tab has to cycle within it —
        // six presses is more than one full cycle of its controls (two recurrence toggles,
        // Cancel, Save).
        for (int tab = 0; tab < 6; tab++)
        {
            await page.Keyboard.PressAsync("Tab");
            bool insideDialog = await commitDialog.EvaluateAsync<bool>(
                "node => node.contains(document.activeElement)");
            Assert.True(insideDialog, $"Tab press {tab + 1} moved focus outside the commit dialog.");
        }

        // Dismissing hands focus back to the grid the paint came from, rather than dropping it on
        // the document body — the keyboard viewer resumes where they left off.
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(commitDialog).Not.ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[role=grid] [role=gridcell]:focus")).ToHaveCountAsync(1);
        await Assertions.Expect(busyCells).ToHaveCountAsync(baselineBusyCount);

        // Paint again from the restored focus position and take the same commit dialog through to
        // Save this time — the create half of this fact continues from here.
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.PressAsync("Shift+ArrowDown");
        await page.Keyboard.PressAsync("Enter");
        await commitDialog.WaitForAsync();

        ILocator commitSave = commitDialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
        await commitSave.FocusAsync();
        await Assertions.Expect(commitSave).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(commitDialog).Not.ToBeVisibleAsync();

        // Two busy gridcells for one 30-minute block — each 15-minute row it spans paints
        // separately, never a single merged DOM cell.
        await Assertions.Expect(busyCells).ToHaveCountAsync(baselineBusyCount + 2);

        // Edit: the availability panel lists every published window, one-off rules last, and each
        // row's own controls are the edit surface (the grid's drag handles are the pointer half of
        // the same operation). Reached and operated by keyboard alone.
        ILocator editWindow = page.GetByRole(AriaRole.Button, new() { Name = "Edit window" }).Last;
        await editWindow.FocusAsync();
        await page.Keyboard.PressAsync("Enter");

        // Stretch the block's end to 09:00 — still inside the visible week, and still ending
        // before the sibling fact's 10:00 series so the two never overlap and merge.
        ILocator endTime = page.GetByLabel("End time", new() { Exact = true });
        await endTime.SelectOptionAsync(new SelectOptionValue { Value = "540" });

        // The block moved rather than duplicating: one rule row still, now four slots long.
        await Assertions.Expect(busyCells).ToHaveCountAsync(baselineBusyCount + 4);
        await Assertions.Expect(page.GetByText(new Regex("08:00.09:00")).First).ToBeVisibleAsync();

        // Delete: a one-time window carries no series, so it goes without a scope question — the
        // weekly confirm is the sibling fact below.
        ILocator deleteWindow = page.GetByRole(AriaRole.Button, new() { Name = "Delete window" }).Last;
        await deleteWindow.FocusAsync();
        await page.Keyboard.PressAsync("Enter");

        await Assertions.Expect(busyCells).ToHaveCountAsync(baselineBusyCount);
    }

    /// <summary>
    /// PHASE8_DESIGN.md §3 S7's calendar SR addition. `WeekGrid`'s selection is drawn with band
    /// borders and a background tint — nothing a screen reader can perceive — so the *extent* of a
    /// growing Shift+Arrow selection has to reach the viewer through the grid's `role=status` live
    /// region instead. This reads that region back off the live DOM at each growth step, which is
    /// the only way to prove the announcement actually changes as the range widens rather than
    /// being emitted once and going stale.
    /// </summary>
    [Fact]
    public async Task SelectionExtentIsAnnouncedAsTheKeyboardSelectionGrows_D139()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        IPage page = organizer.Page;

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/calendar");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // The grid's own status region, not the page's other live regions (the early-hours notice
        // and the app chrome's notification counter both carry one).
        ILocator gridStatus = page.Locator(
            "[data-testid=screen-calendar] [role=status]:not([data-testid=early-hours-status])");

        await page.GetByRole(AriaRole.Gridcell).First.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(gridStatus).ToHaveTextAsync(new Regex("^Selection started at "));

        // One slot of growth: the extent is now a range, and its end is the first slot boundary.
        await page.Keyboard.PressAsync("Shift+ArrowDown");
        await Assertions.Expect(gridStatus).ToHaveTextAsync(new Regex(@"^Selected .+ to .+\.$"));
        string afterOne = await gridStatus.InnerTextAsync();

        // A second growth must produce a *different* announcement — a live region that repeated the
        // first message would leave the viewer with no idea the range had widened at all.
        await page.Keyboard.PressAsync("Shift+ArrowDown");
        await Assertions.Expect(gridStatus).Not.ToHaveTextAsync(afterOne);
        await Assertions.Expect(gridStatus).ToHaveTextAsync(new Regex(@"^Selected .+ to .+\.$"));

        // Escape leaves the calendar as it was found — nothing committed, and the cancellation is
        // itself announced rather than silently dropping the selection.
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(gridStatus).ToHaveTextAsync("Selection canceled.");
    }

    /// <summary>
    /// PHASE8_DESIGN.md §3 S7's agenda half (D-104): the agenda is the non-spatial view of the same
    /// range, so it is the surface a screen-reader user actually reads — this pins that it is a
    /// named list of list items whose text carries date, time span and title as words, never a
    /// layout table or a set of positioned divs.
    /// </summary>
    [Fact]
    public async Task AgendaViewIsANamedListWhoseRowsCarryTheirOwnText_D139()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        IPage page = organizer.Page;

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/app");
        await SeedWeeklySeriesAsync(page);

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/calendar/agenda");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Both views stay reachable from the calendar itself, not by URL alone.
        await Assertions
            .Expect(page.GetByRole(AriaRole.Tab, new() { Name = "Agenda" }))
            .ToHaveAttributeAsync("aria-selected", "true");

        ILocator agenda = page.GetByRole(AriaRole.List, new() { Name = "Agenda" });
        await agenda.WaitForAsync();

        ILocator entries = agenda.GetByRole(AriaRole.Listitem);
        Assert.True(
            await entries.CountAsync() >= 1,
            "the seeded series should put at least one entry in the agenda");

        // Every row is self-describing text: a date, a start–end span, and a title. Read as one
        // string because that is how a screen reader announces the row.
        string firstRow = await entries.First.InnerTextAsync();
        Assert.Matches(new Regex(@"\d"), firstRow);
        Assert.Contains("–", firstRow, StringComparison.Ordinal);
        Assert.False(
            string.IsNullOrWhiteSpace(firstRow),
            "an agenda row must not depend on anything but its own text");

        // The spatial view is one keyboard activation away, over the same range.
        await page.GetByRole(AriaRole.Tab, new() { Name = "Week" }).ClickAsync();
        await page.GetByRole(AriaRole.Grid).WaitForAsync();
    }

    [Fact]
    public async Task OccurrenceScopeDialogIsReachableWithoutAPointer_D143()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        IPage page = organizer.Page;

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/app");
        await SeedWeeklySeriesAsync(page);

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/calendar");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // The panel lists weekly rules first, so the first row is a series occurrence — the one
        // case whose edit has to ask about scope. Opened by keyboard alone.
        ILocator editWindow = page.GetByRole(AriaRole.Button, new() { Name = "Edit window" }).First;
        await editWindow.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.GetByLabel("Start time", new() { Exact = true }).SelectOptionAsync(
            new SelectOptionValue { Value = "555" });

        ILocator scopeDialog = page.GetByRole(AriaRole.Dialog);
        await scopeDialog.GetByRole(AriaRole.Heading, new() { Name = "Which occurrences?" }).WaitForAsync();

        ILocator onlyThisRadio = page.GetByRole(AriaRole.Radio, new() { Name = "Only this occurrence" });
        ILocator thisAndFutureRadio = page.GetByRole(AriaRole.Radio, new() { Name = "This and future occurrences" });
        await Assertions.Expect(onlyThisRadio).ToBeCheckedAsync();
        await Assertions.Expect(thisAndFutureRadio).Not.ToBeCheckedAsync();

        // Reachable and operable with the keyboard alone: focus the second option, select it with
        // Space (native radio-group semantics), then reach and activate Confirm the same way.
        await thisAndFutureRadio.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await Assertions.Expect(thisAndFutureRadio).ToBeCheckedAsync();

        ILocator confirmButton = page.GetByRole(AriaRole.Button, new() { Name = "Confirm" });
        await confirmButton.FocusAsync();
        await page.Keyboard.PressAsync("Enter");

        // The scope choice reaches the write it gates — the `thisAndFuture` body shape itself is
        // proven at the unit layer (`lovable/CalendarShell.test.tsx`'s
        // `saves the thisAndFuture scope as the series shape...` test); this fact's job is the
        // dialog's own pointer-free reachability, so it stops once the edit has landed.
        await Assertions.Expect(scopeDialog).Not.ToBeVisibleAsync();
        await page.GetByText("Window updated").WaitForAsync();
    }
}
