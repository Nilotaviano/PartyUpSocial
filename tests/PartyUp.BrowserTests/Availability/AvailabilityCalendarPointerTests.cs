using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §11 "Browser — WebKit pointer lane (S6, P5-TOUCH-01)": the A-018 pointer risks
/// proven on WebKit, not only Chromium — long-press selection begins without cancelling page scroll
/// (the `touch-action:none` conflict), drag auto-scroll reaches an off-screen row, a long-press that
/// becomes a scroll commits nothing, and 15-minute snapping holds under a touch drag exactly as it
/// does under keyboard entry (D-071).
/// <para>
/// Runs its own WebKit instance with a touch-emulating context, the same shape
/// <see cref="WebKitLaneSmokeTests"/> already established, rather than the collection's shared
/// <see cref="PlaywrightFixture.Browser"/> — a pointer-lane test proving nothing about
/// WebKit specifically would defeat its own purpose. <c>[Trait("Browser","WebKit")]</c> routes this
/// class to the `browser` CI job's second step exclusively (A-082); that step's `dotnet test`
/// invocation is a separate process from the Chromium step's, so it carries its own fresh
/// `register-ip` budget (5/hour/IP) — each fact below registers its own account, four total, safely
/// under that ceiling (unlike the Chromium step, whose budget the sibling
/// <see cref="AvailabilityCalendarFlowTests"/> class documents as already fully spent).
/// </para>
/// <para>
/// The 390×844 context is below the `sm` breakpoint, so CALENDAR_DESIGN.md §9 S5's day window
/// applies: the grid renders three columns per row, not seven. The window is derived, never
/// seeded — the fixture registers a fresh account and loads `/calendar` at `weekOffset 0` on
/// whatever the real current date is, and S5's initial-window rule puts the window start at
/// `clamp(todayIndex, 0, 4)`. That clamp always keeps today inside the window, so which weekday
/// each column holds is not fixed, but the window is always exactly three columns wide and
/// always present. Every day index below is therefore a window-relative column (0..2) and
/// <see cref="CellIndex"/> strides by three.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Browser", "WebKit")]
[Collection(PlaywrightSuite.Name)]
public sealed class AvailabilityCalendarPointerTests(PlaywrightFixture fixture)
{
    // The constructor-injected `fixture` is the one real, already-`InitializeAsync`'d shared
    // instance [Collection] provides (its mail-drop path, API process etc. all live) — its
    // `RegisterAndOnboardAsync` is browser-agnostic (takes an `IBrowserContext` parameter, never
    // touches `fixture.Browser`), so it works unmodified against the WebKit context this class
    // creates for itself below.
    /// <summary>
    /// The commit dialog's accessible name is the painted range (`commit.range`), formatted in the
    /// viewer's locale — matched on the "time separator time" shape so it survives any range value.
    /// </summary>
    private static readonly Regex CommitRangePattern = new(@"\d{1,2}:\d{2}.*\d{1,2}:\d{2}");

    private const int LongPressSettleMs = 550;
    private const int ShortPressMs = 100;
    private const int AutoScrollSettleMs = 600;

    private async Task<(IPlaywright Playwright, IBrowser Browser, IPage Page)> StartTouchSessionAsync(
        string emailLocalPart)
    {
        IPlaywright playwright = await Playwright.CreateAsync();
        IBrowser browser = await playwright.Webkit.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            HasTouch = true,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        });

        RegisteredUser user = await fixture.RegisterAndOnboardAsync(context, emailLocalPart, "Pointer Tester");

        return (playwright, browser, user.Page);
    }

    /// <summary>
    /// Dispatches a real <c>PointerEvent</c> (<c>pointerType: "touch"</c>) at the given gridcell —
    /// or at an explicit viewport point when <paramref name="clientY"/> is supplied, which is what
    /// the auto-scroll fact needs to reach a coordinate near the viewport's own bottom edge,
    /// independent of any one cell's on-screen position.
    /// </summary>
    private static async Task DispatchTouchPointerEventAsync(
        IPage page, string type, int cellIndex, double? clientY = null)
    {
        await page.EvaluateAsync(
            """
            ({ type, cellIndex, clientY }) => {
                const cells = document.querySelectorAll('[role="gridcell"]');
                const el = cells[cellIndex];
                const rect = el.getBoundingClientRect();
                const x = rect.left + rect.width / 2;
                const y = clientY ?? (rect.top + rect.height / 2);
                el.dispatchEvent(new PointerEvent(type, {
                    bubbles: true,
                    cancelable: true,
                    pointerId: 1,
                    pointerType: 'touch',
                    clientX: x,
                    clientY: y,
                }));
            }
            """,
            new { type, cellIndex, clientY });
    }

    private const int DayWindowLength = 3;

    /// <summary>
    /// Row-major gridcell index for (column, slot), where <paramref name="column"/> is the
    /// window-relative day column — <see cref="DayWindowLength"/> gridcells per row at this
    /// viewport (see `WeekGrid.tsx`'s `dayWindow`).
    /// </summary>
    private static int CellIndex(int column, int slot) => (slot * DayWindowLength) + column;

    /// <summary>
    /// D-009/§7: a painted range is a proposal — the commit dialog's one-time/weekly choice is
    /// what actually creates anything. The touch facts below care about the geometry the drag
    /// produced, not about recurrence, so each confirms at the dialog's "Just this time" default.
    /// </summary>
    private static async Task ConfirmCommitDialogAsync(IPage page)
    {
        ILocator dialog = page.GetByRole(AriaRole.Dialog, new() { NameRegex = CommitRangePattern });
        await dialog.WaitForAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task TouchLongPressSelectsWithoutBreakingScroll_D139()
    {
        (IPlaywright playwright, IBrowser browser, IPage page) =
            await StartTouchSessionAsync("touchlongpress");
        try
        {
            await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/calendar");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.GetByRole(AriaRole.Grid).WaitForAsync();
            ILocator grid = page.GetByRole(AriaRole.Grid);

            int cellIndex = CellIndex(column: 0, slot: 0);
            await DispatchTouchPointerEventAsync(page, "pointerdown", cellIndex);

            // Before the long-press threshold, the container has not opted out of native
            // panning — an ordinary touch (about to become a scroll, proven by the sibling fact
            // below) is never blocked from scrolling by this component.
            await page.WaitForTimeoutAsync(ShortPressMs);
            await Assertions.Expect(grid).ToHaveAttributeAsync("data-touch-dragging", "false");

            // Past the threshold, the press has become a selection — and only now does the
            // container suppress native touch panning (the `touch-action:none` conflict this
            // gate exists to prove is resolved: scroll is never blocked before a hold is real).
            await page.WaitForTimeoutAsync(LongPressSettleMs - ShortPressMs);
            await Assertions.Expect(grid).ToHaveAttributeAsync("data-touch-dragging", "true");
            await page.GetByRole(AriaRole.Status).GetByText("Selection started").WaitForAsync();

            await DispatchTouchPointerEventAsync(page, "pointerup", cellIndex);

            // The commit dialog is settled first: it is modal, so while it is up Radix marks the
            // rest of the page `aria-hidden` and the grid drops out of every role-based locator.
            await ConfirmCommitDialogAsync(page);
            await Assertions.Expect(grid).ToHaveAttributeAsync("data-touch-dragging", "false");
            await Assertions.Expect(page.GetByRole(AriaRole.Gridcell).Nth(cellIndex))
                .ToHaveAttributeAsync("data-state", "busy");
        }
        finally
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }

    [Fact]
    public async Task DragAutoScrollReachesAnOffScreenRow_D139()
    {
        (IPlaywright playwright, IBrowser browser, IPage page) =
            await StartTouchSessionAsync("touchautoscroll");
        try
        {
            await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/calendar");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.GetByRole(AriaRole.Grid).WaitForAsync();
            ILocator grid = page.GetByRole(AriaRole.Grid);

            // The window's second column (each fact runs in its own account and browser, so the
            // columns only have to be readable, not disjoint) — starting near the top so there is
            // real distance left to auto-scroll through.
            int startCellIndex = CellIndex(column: 1, slot: 0);
            await DispatchTouchPointerEventAsync(page, "pointerdown", startCellIndex);
            await page.WaitForTimeoutAsync(LongPressSettleMs);

            double initialScrollY = await page.EvaluateAsync<double>("() => window.scrollY");

            // A move whose clientY sits inside the viewport's own bottom-edge auto-scroll band
            // (`AUTO_SCROLL_EDGE_PX` in `WeekGrid.tsx`) — an off-screen row below the fold, which
            // is the entire point: the drag never has to (and in this headless viewport cannot)
            // visually reach that row on its own. The grid has no scrollport of its own any more —
            // it renders at natural height and the document scrolls — so the page is what moves.
            double viewportBottom = await page.EvaluateAsync<double>("() => window.innerHeight");
            await DispatchTouchPointerEventAsync(
                page, "pointermove", startCellIndex, clientY: viewportBottom - 5);

            await page.WaitForTimeoutAsync(AutoScrollSettleMs);
            double scrolledScrollY = await page.EvaluateAsync<double>("() => window.scrollY");
            Assert.True(
                scrolledScrollY > initialScrollY,
                $"Expected the page to auto-scroll past {initialScrollY}, stayed at {scrolledScrollY}.");

            await DispatchTouchPointerEventAsync(page, "pointerup", startCellIndex);

            // The drag painted a range, so the D-009 commit dialog now opens on pointerup. This
            // fact is about the auto-scroll, not about creating anything, so the proposal is
            // discarded — and it has to be dismissed before the grid is asserted against at all:
            // while that modal is up Radix `aria-hidden`s the page and no role locator resolves.
            ILocator commitDialog = page.GetByRole(AriaRole.Dialog, new() { NameRegex = CommitRangePattern });
            await commitDialog.WaitForAsync();
            await commitDialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(commitDialog).Not.ToBeVisibleAsync();

            await Assertions.Expect(grid).ToHaveAttributeAsync("data-touch-dragging", "false");
        }
        finally
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }

    [Fact]
    public async Task ALongPressThatBecomesAScrollCommitsNothing_D139()
    {
        (IPlaywright playwright, IBrowser browser, IPage page) =
            await StartTouchSessionAsync("touchscrollcancel");
        try
        {
            await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/calendar");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.GetByRole(AriaRole.Grid).WaitForAsync();
            ILocator grid = page.GetByRole(AriaRole.Grid);

            // The window's third column.
            int cellIndex = CellIndex(column: 2, slot: 0);
            await DispatchTouchPointerEventAsync(page, "pointerdown", cellIndex);

            // Movement well past the cancel threshold (`MOVE_CANCEL_PX` in `WeekGrid.tsx`),
            // dispatched before the long-press timer would fire — a real scroll gesture
            // overtaking a still-pending press.
            await page.WaitForTimeoutAsync(ShortPressMs);
            await DispatchTouchPointerEventAsync(page, "pointermove", cellIndex, clientY: 0);

            // Past the original long-press threshold, the timer must not have fired.
            await page.WaitForTimeoutAsync(LongPressSettleMs);
            await Assertions.Expect(grid).ToHaveAttributeAsync("data-touch-dragging", "false");

            await DispatchTouchPointerEventAsync(page, "pointerup", cellIndex);

            await Assertions.Expect(grid).ToHaveAttributeAsync("data-touch-dragging", "false");
            await Assertions.Expect(page.GetByRole(AriaRole.Gridcell).Nth(cellIndex))
                .ToHaveAttributeAsync("data-state", "free");
            // The grid's own live region announced nothing — no "Selection started". Addressed by
            // CSS rather than by role alone: the app chrome carries a role="status" region (the
            // notification bell) and the calendar screen itself now carries two (the grid's and
            // the early-hours announcement, `data-testid="early-hours-status"`), so both the
            // bare-page and the screen-scoped role locators are ambiguous.
            await Assertions.Expect(
                    page.Locator(
                        "[data-testid='screen-calendar'] [role='status']:not([data-testid='early-hours-status'])"))
                .ToHaveTextAsync("");

            // And nothing was proposed either: a reclaimed gesture never reaches the D-009 commit
            // step, so no commit dialog is waiting to be confirmed.
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        }
        finally
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }

    [Fact]
    public async Task FifteenMinuteSnappingHoldsUnderTouchDrag_D071()
    {
        (IPlaywright playwright, IBrowser browser, IPage page) =
            await StartTouchSessionAsync("touchsnapping");
        try
        {
            await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/calendar");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.GetByRole(AriaRole.Grid).WaitForAsync();

            // The window's first column, slots 0..4 — a 1-hour (4 × 15-minute) drag.
            int startCellIndex = CellIndex(column: 0, slot: 0);
            int endCellIndex = CellIndex(column: 0, slot: 4);
            await DispatchTouchPointerEventAsync(page, "pointerdown", startCellIndex);
            await page.WaitForTimeoutAsync(LongPressSettleMs);
            await DispatchTouchPointerEventAsync(page, "pointermove", endCellIndex);
            await DispatchTouchPointerEventAsync(page, "pointerup", endCellIndex);
            await ConfirmCommitDialogAsync(page);

            // Exactly the five slots the drag spanned are now busy — a touch drag lands on the
            // same 15-minute grid the keyboard path is quantized to by construction
            // (`WeekGridPage.test.tsx`'s keyboard-entry snapping row proves that half); no
            // sub-slot precision ever reaches the commit.
            for (int slot = 0; slot <= 4; slot++)
            {
                await Assertions.Expect(page.GetByRole(AriaRole.Gridcell).Nth(CellIndex(0, slot)))
                    .ToHaveAttributeAsync("data-state", "busy");
            }

            await Assertions.Expect(page.GetByRole(AriaRole.Gridcell).Nth(CellIndex(0, 5)))
                .ToHaveAttributeAsync("data-state", "free");
        }
        finally
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }
}
