using System.Globalization;
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
    /// The commit dialog's accessible name is the painted range (`commit.range`), formatted in the
    /// viewer's locale — matched on the "time separator time" shape so it survives any range value.
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
            _organizer ??= await fixture.RegisterAndOnboardAsync(
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
    /// fact above passes at any height, so it is not a tripwire against the Gabarito/Nunito Sans
    /// font swap moving row metrics. This fact pins the durable floor instead — every row's cells
    /// are at least 44px tall (WCAG 2.2 target-size) and every row is the same height as the first
    /// (no pixel-pinned baseline, since fonts legitimately move metrics slightly).
    /// </summary>
    [Fact]
    public async Task GridCellsMeetGeometryFloor_D132()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        IPage page = organizer.Page;

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/calendar");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        ILocator grid = page.GetByRole(AriaRole.Grid);
        ILocator rows = grid.Locator("[role=row]");
        int rowCount = await rows.CountAsync();
        Assert.True(rowCount >= 3, "expected several time-slot rows in the visible week");

        double? firstRowHeight = null;
        for (int rowIndex = 1; rowIndex < rowCount; rowIndex++)
        {
            ILocator cells = rows.Nth(rowIndex).Locator("[role=gridcell]");
            int cellCount = await cells.CountAsync();
            Assert.True(cellCount >= 1, $"row {rowIndex} has no gridcells");

            for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
            {
                LocatorBoundingBoxResult? box = await cells.Nth(cellIndex).BoundingBoxAsync();
                Assert.NotNull(box);
                Assert.True(
                    box!.Height >= 44,
                    $"row {rowIndex} cell {cellIndex} is {box.Height}px tall, expected >= 44px");

                firstRowHeight ??= box.Height;
                Assert.True(
                    Math.Abs(box.Height - firstRowHeight.Value) < 1,
                    $"row {rowIndex} cell {cellIndex} height {box.Height} differs from row 1's {firstRowHeight}");
            }
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
        ILocator busyCells = page.GetByRole(AriaRole.Gridcell, new() { NameRegex = new Regex("Busy") });
        int baselineBusyCount = await busyCells.CountAsync();

        // The same cells addressed structurally. While the modal commit dialog is open Radix
        // marks the rest of the page `aria-hidden`, which removes it from the accessibility tree
        // and therefore from every role-based locator — so the "nothing is created yet" check
        // below, the only one that runs with the dialog up, has to read the DOM instead.
        ILocator busyCellsInDom = page.Locator("[role=gridcell][data-busy=true]");
        int baselineBusyInDom = await busyCellsInDom.CountAsync();

        // Create: focus the grid's one tab stop (08:00 on the week's first day, the clamped
        // viewport's first row — always earlier than the sibling fact's fixed 10:00 series, so
        // it sorts first in DOM order below regardless of run order), then keyboard alone —
        // Enter starts a selection, Shift+ArrowDown extends it by one 15-minute slot (two
        // grid rows), Enter ends the paint (P5-CAL-01). This first dialog is Escaped away
        // to prove the trap and focus return; the block actually saved is the 08:15–08:45
        // repaint below.
        ILocator firstCell = page.GetByRole(AriaRole.Gridcell).First;
        await firstCell.FocusAsync();
        await Assertions.Expect(firstCell).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.PressAsync("Shift+ArrowDown");
        await page.Keyboard.PressAsync("Enter");

        // D-009/§7: the painted range is only a proposal — nothing is created until the commit
        // dialog's one-time/weekly choice is confirmed. Keyboard-only, so the choice is left at
        // its "Just this time" default (a one-time block, which the edit half below depends on)
        // and Save is reached and activated without a pointer.
        // The dialog is named by the painted range itself, so it is matched on the
        // time-range shape rather than on a fixed title string.
        ILocator commitDialog = page.GetByRole(AriaRole.Dialog, new() { NameRegex = CommitRangePattern });
        await commitDialog.WaitForAsync();
        await Assertions.Expect(busyCellsInDom).ToHaveCountAsync(baselineBusyInDom);
        await Assertions
            .Expect(commitDialog.GetByRole(AriaRole.Radio, new() { Name = "Just this time" }))
            .ToBeCheckedAsync();

        // WCAG 2.4.3/2.1.2: the popover is modal, so Tab has to cycle within it — six presses is
        // more than one full cycle of its controls (two recurrence radios, Cancel, Save).
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
        await Assertions.Expect(busyCellsInDom).ToHaveCountAsync(baselineBusyInDom);

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
        // separately (`mergedAt` in `WeekGrid.tsx`), never a single merged DOM cell.
        await Assertions.Expect(busyCells).ToHaveCountAsync(baselineBusyCount + 2);

        // Edit: Enter on the busy cell opens the editor directly (a one-time block carries no
        // series, so no scope choice applies — that half is the sibling fact below). Read the
        // dialog's own prefilled value back rather than independently recomputing the grid's
        // displayed week, and shift one hour later on the same day so the edited block stays
        // inside the still-visible week and still sorts before — and never overlaps, which would
        // merge the two bands and change the busy-cell count — the sibling fact's 10:00 series.
        await busyCells.First.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        ILocator editDialog = page.GetByRole(AriaRole.Dialog);
        await editDialog.GetByText("Edit availability").WaitForAsync();

        // Dialog-scoped and label-exact: the grid's own edge-resize handles are labelled
        // "Change start time"/"Change end time", which a page-wide substring label match also
        // resolves (strict-mode violation), and `aria-hidden` does not filter label locators.
        ILocator startField = editDialog.GetByLabel("Start time", new() { Exact = true });
        ILocator endField = editDialog.GetByLabel("End time", new() { Exact = true });
        DateTime originalStart = DateTime.ParseExact(
            await startField.InputValueAsync(), "yyyy-MM-dd\\THH:mm", CultureInfo.InvariantCulture);
        DateTime editedStart = originalStart.AddHours(1);
        DateTime editedEnd = editedStart.AddMinutes(30);
        await startField.FillAsync(editedStart.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));
        await endField.FillAsync(editedEnd.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));

        ILocator saveButton = page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
        await saveButton.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(editDialog).Not.ToBeVisibleAsync();

        // Still the same count — the edit moved the block, it did not duplicate it.
        await Assertions.Expect(busyCells).ToHaveCountAsync(baselineBusyCount + 2);

        // Delete: focus the (moved) busy cell, open it, reach Delete with the keyboard alone.
        await busyCells.First.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        ILocator deleteDialog = page.GetByRole(AriaRole.Dialog);
        await deleteDialog.GetByText("Edit availability").WaitForAsync();
        // Scoped to the editor and name-exact: the availability summary panel renders a per-row
        // delete button whose accessible name is "Delete <rule>", which a page-wide, non-exact
        // "Delete" match would also resolve (strict-mode violation).
        ILocator deleteButton = deleteDialog.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true });
        await deleteButton.FocusAsync();
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
        ILocator gridStatus = page.Locator("[role=grid]").Locator("xpath=..").GetByRole(AriaRole.Status);

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

        ILocator busyCell = page.GetByRole(AriaRole.Gridcell, new() { NameRegex = new Regex("Busy") }).First;
        await busyCell.WaitForAsync();
        await busyCell.FocusAsync();
        await page.Keyboard.PressAsync("Enter");

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

        // The scope choice reaches the editor it gates — the flow that follows is D-143's
        // `thisAndFuture` save path, proven end to end at the unit layer
        // (`WeekGridPage.test.tsx`'s `saves the thisAndFuture scope end to end...` test); this
        // fact's job is the dialog's own pointer-free reachability, so it stops once that editor
        // opens and leaves without saving.
        ILocator editDialog = page.GetByRole(AriaRole.Dialog);
        await editDialog.GetByText("Edit availability").WaitForAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(editDialog).Not.ToBeVisibleAsync();
    }
}
