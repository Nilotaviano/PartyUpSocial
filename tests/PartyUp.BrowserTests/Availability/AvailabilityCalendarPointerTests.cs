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
/// Chromium <see cref="PlaywrightFixture.Browser"/> — a pointer-lane test proving nothing about
/// WebKit specifically would defeat its own purpose. <c>[Trait("Browser","WebKit")]</c> routes this
/// class to the `browser` CI job's second step exclusively (A-082); that step's `dotnet test`
/// invocation is a separate process from the Chromium step's, so it carries its own fresh
/// `register-ip` budget (5/hour/IP) — each fact below registers its own account, four total, safely
/// under that ceiling (unlike the Chromium step, whose budget the sibling
/// <see cref="AvailabilityCalendarFlowTests"/> class documents as already fully spent).
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
    /// the auto-scroll fact needs to reach a coordinate near the scroll container's own edge,
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

    /// <summary>Row-major gridcell index for (day, slot) — 7 gridcells per row (see `WeekGrid.tsx`).</summary>
    private static int CellIndex(int day, int slot) => (slot * 7) + day;

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

            int cellIndex = CellIndex(day: 0, slot: 0);
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

            // Day 1's column (never touched by the sibling long-press fact, which uses day 0) —
            // starting near the top so there is real distance left to auto-scroll through.
            int startCellIndex = CellIndex(day: 1, slot: 0);
            await DispatchTouchPointerEventAsync(page, "pointerdown", startCellIndex);
            await page.WaitForTimeoutAsync(LongPressSettleMs);

            double initialScrollTop = await grid.EvaluateAsync<double>("el => el.scrollTop");

            // A move whose clientY sits inside the container's own bottom-edge auto-scroll band
            // (`AUTO_SCROLL_EDGE_PX` in `WeekGrid.tsx`) — an off-screen row below the fold, which
            // is the entire point: the drag never has to (and in this headless viewport cannot)
            // visually reach that row on its own.
            double containerBottom = await grid.EvaluateAsync<double>(
                "el => el.getBoundingClientRect().bottom");
            await DispatchTouchPointerEventAsync(
                page, "pointermove", startCellIndex, clientY: containerBottom - 5);

            await page.WaitForTimeoutAsync(AutoScrollSettleMs);
            double scrolledScrollTop = await grid.EvaluateAsync<double>("el => el.scrollTop");
            Assert.True(
                scrolledScrollTop > initialScrollTop,
                $"Expected the grid to auto-scroll past {initialScrollTop}, stayed at {scrolledScrollTop}.");

            await DispatchTouchPointerEventAsync(page, "pointerup", startCellIndex);
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

            // Day 2's column — isolated from the other facts' cells.
            int cellIndex = CellIndex(day: 2, slot: 0);
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
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("");
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

            // Day 3's column, slots 0..4 — a 1-hour (4 × 15-minute) drag.
            int startCellIndex = CellIndex(day: 3, slot: 0);
            int endCellIndex = CellIndex(day: 3, slot: 4);
            await DispatchTouchPointerEventAsync(page, "pointerdown", startCellIndex);
            await page.WaitForTimeoutAsync(LongPressSettleMs);
            await DispatchTouchPointerEventAsync(page, "pointermove", endCellIndex);
            await DispatchTouchPointerEventAsync(page, "pointerup", endCellIndex);

            // Exactly the five slots the drag spanned are now busy — a touch drag lands on the
            // same 15-minute grid the keyboard path is quantized to by construction
            // (`WeekGridPage.test.tsx`'s keyboard-entry snapping row proves that half); no
            // sub-slot precision ever reaches the commit.
            for (int slot = 0; slot <= 4; slot++)
            {
                await Assertions.Expect(page.GetByRole(AriaRole.Gridcell).Nth(CellIndex(3, slot)))
                    .ToHaveAttributeAsync("data-state", "busy");
            }

            await Assertions.Expect(page.GetByRole(AriaRole.Gridcell).Nth(CellIndex(3, 5)))
                .ToHaveAttributeAsync("data-state", "free");
        }
        finally
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }
}
