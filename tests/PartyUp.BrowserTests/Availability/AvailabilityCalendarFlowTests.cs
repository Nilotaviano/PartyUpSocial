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
/// once) across both facts below — never a second.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class AvailabilityCalendarFlowTests(PlaywrightFixture fixture)
{
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

        // Create: focus the grid's one tab stop (00:00 on the week's first day — always earlier
        // than the sibling fact's fixed 10:00 series, so it sorts first in DOM order below
        // regardless of run order), then keyboard alone — Enter starts a selection,
        // Shift+ArrowDown extends it by one 15-minute slot (a 00:00–00:30 range, two grid rows),
        // Enter commits (P5-CAL-01).
        ILocator firstCell = page.GetByRole(AriaRole.Gridcell).First;
        await firstCell.FocusAsync();
        await Assertions.Expect(firstCell).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.PressAsync("Shift+ArrowDown");
        await page.Keyboard.PressAsync("Enter");

        // Two busy gridcells for one 30-minute block — each 15-minute row it spans paints
        // separately (`mergedAt` in `WeekGrid.tsx`), never a single merged DOM cell.
        await Assertions.Expect(busyCells).ToHaveCountAsync(baselineBusyCount + 2);

        // Edit: Enter on the busy cell opens the editor directly (a one-time block carries no
        // series, so no scope choice applies — that half is the sibling fact below). Read the
        // dialog's own prefilled value back rather than independently recomputing the grid's
        // displayed week, and shift two hours later on the same day so the edited block stays
        // inside the still-visible week and still sorts before the sibling fact's 10:00 series.
        await busyCells.First.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        ILocator editDialog = page.GetByRole(AriaRole.Dialog);
        await editDialog.GetByText("Edit availability").WaitForAsync();

        ILocator startField = page.GetByLabel("Start time");
        ILocator endField = page.GetByLabel("End time");
        DateTime originalStart = DateTime.ParseExact(
            await startField.InputValueAsync(), "yyyy-MM-dd\\THH:mm", CultureInfo.InvariantCulture);
        DateTime editedStart = originalStart.AddHours(2);
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
        await page.GetByRole(AriaRole.Dialog).GetByText("Edit availability").WaitForAsync();
        ILocator deleteButton = page.GetByRole(AriaRole.Button, new() { Name = "Delete" });
        await deleteButton.FocusAsync();
        await page.Keyboard.PressAsync("Enter");

        await Assertions.Expect(busyCells).ToHaveCountAsync(baselineBusyCount);
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
