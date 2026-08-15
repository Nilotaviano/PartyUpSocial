using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Tour;

/// <summary>
/// TOUR_DESIGN.md §6: the tour's own browser coverage reaches the overlay through Replay — the
/// fixture skip (`PlaywrightFixture.RegisterAndOnboardAsync`) makes auto-open unreachable for lane
/// accounts by design, so every fact here registers once, dismisses the auto-tour via the fixture,
/// then walks: register → `/settings/account` → Replay → overlay.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightCalendarSuite.Name)]
public sealed class TourFlowTests(PlaywrightFixture fixture)
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static RegisteredUser? _user;

    private async Task<RegisteredUser> GetUserAsync()
    {
        if (_user is not null)
        {
            return _user;
        }

        await SetupLock.WaitAsync();
        try
        {
            _user ??= await fixture.RegisterAndOnboardAsync(
                await fixture.NewContextAsync(), "touruser", "Tour User");
            return _user;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    private static async Task OpenViaReplayAsync(IPage page)
    {
        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/settings/account");
        await page.GetByRole(AriaRole.Button, new() { Name = "Replay the tour" }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).WaitForAsync();
    }

    /// <summary>
    /// TOUR_DESIGN.md §3: Radix's own focus trap puts the first tabbable footer control (Skip) under
    /// focus on open; one more Tab reaches Next/Finish — this walks all eight stops with Tab+Enter
    /// alone, never a mouse click, ending on the real completion POST via Finish.
    /// </summary>
    [Fact]
    public async Task KeyboardOnly_WalksAllEightStopsViaReplayAndFinishes_D182()
    {
        RegisteredUser user = await GetUserAsync();
        await OpenViaReplayAsync(user.Page);

        string[] expectedNames =
        [
            "Home", "Friends", "Sessions", "Polls", "Calendar", "Discover", "Notifications", "Settings",
        ];

        for (int step = 0; step < expectedNames.Length - 1; step++)
        {
            await user.Page.GetByText($"Step {step + 1} of 8: {expectedNames[step]}").WaitForAsync();
            if (step == 0)
            {
                // Skip is the first tabbable footer control (Radix's own open-focus default); one
                // Tab reaches Next.
                await user.Page.Keyboard.PressAsync("Tab");
            }

            // Every later step: React reuses the same Next button DOM node across the re-render, so
            // focus is already retained there from the previous Enter — a second Tab here would
            // wrap the trap back to Skip instead.
            await user.Page.Keyboard.PressAsync("Enter");
        }

        await user.Page.GetByText($"Step {expectedNames.Length} of 8: {expectedNames[^1]}").WaitForAsync();

        // Focus is already retained on the same button (now labeled "Finish") from the last Enter
        // above — no Tab needed. The response barrier wraps the Enter that presses it — arming the
        // wait AFTER the keypress would race a fast 204 (`PollVotingFlowTests.cs`'s own
        // click-and-await idiom).
        IResponse response = await user.Page.RunAndWaitForResponseAsync(
            () => user.Page.Keyboard.PressAsync("Enter"),
            candidate => candidate.Url.Contains("/api/v1/me/tour-completed", StringComparison.Ordinal) &&
                candidate.Request.Method == "POST");
        Assert.Equal(204, response.Status);
        await Assertions.Expect(user.Page.GetByRole(AriaRole.Dialog)).Not.ToBeVisibleAsync();

        // TOUR_DESIGN.md §3's onCloseAutoFocus override: no trigger element exists, so focus lands
        // on the Home link instead.
        await Assertions.Expect(user.Page.Locator("[data-tour='home']:visible")).ToBeFocusedAsync();
    }

    /// <summary>
    /// TOUR_DESIGN.md §3's "position" channel of "ring + position + card arrow": the card sits near
    /// the active anchor (`TourOverlay.tsx`'s `computeCardPosition`), not pinned to a fixed screen
    /// location — a regression back to a static card would be caught by nothing else (jsdom's
    /// degenerate rects make this unprovable in vitest). A full-width viewport keeps every anchor
    /// on-screen (the fixed 5-tab bottom bar and compact TopBar clip nothing at this width), so
    /// this is purely a geometry check: the card actually moves across three stops
    /// (Home and Settings at the two clamped horizontal extremes, Polls — step 4 — in the
    /// unclamped middle of the nav row, closing the blind spot the two-extreme version left), and
    /// each sampled position mirrors <see cref="AssertCardCenteredOnAnchor"/>'s copy of
    /// `computeCardPosition`'s own clamped-center-under-anchor arithmetic for its anchor. Also
    /// probes (§3/G5, at this test's default 1280×800 point) that the card's own teaching text
    /// never needs its `overflow-y-auto` scroll escape hatch in ordinary use.
    /// </summary>
    [Fact]
    public async Task CardPlacement_TracksTheActiveAnchorAndMovesBetweenDistantSteps_D182()
    {
        RegisteredUser user = await GetUserAsync();
        await user.Page.SetViewportSizeAsync(1280, 800);
        await OpenViaReplayAsync(user.Page);

        ILocator homeAnchor = user.Page.Locator("[data-tour='home']:visible");
        ILocator pollsAnchor = user.Page.Locator("[data-tour='polls']:visible");
        ILocator settingsAnchor = user.Page.Locator("[data-tour='settings']:visible");
        int innerWidth = await user.Page.EvaluateAsync<int>("() => window.innerWidth");

        // Auto-retrying — waits out the anchor's own settle the same way the narrow-viewport test's
        // `ToBeInViewportAsync(Ratio = 1)` does, before any card box is sampled.
        await Assertions.Expect(homeAnchor).ToBeInViewportAsync();
        double homeAnchorCenterX = await AnchorCenterXAsync(homeAnchor);
        double homeCardCenterX = await StableCardCenterXAsync(user.Page);

        // G5 probe: the card's content isn't clipped by its own `overflow-y-auto` at the ordinary
        // desktop viewport this whole test otherwise samples at. Probed at every sampled stop —
        // the longest teaching (Settings), not the shortest (Home), is the one that would clip
        // first. Each read follows a settled box sample, so there is no separate race to guard.
        async Task AssertNotClippedAsync(string stopName)
        {
            bool notClipped = await user.Page.EvaluateAsync<bool>(
                "() => { const el = document.querySelector('[data-testid=\"tour-overlay\"]'); " +
                "return el.scrollHeight <= el.clientHeight; }");
            Assert.True(notClipped, $"Tour card content at '{stopName}' should not need its overflow-y-auto escape hatch at 1280x800.");
        }

        await AssertNotClippedAsync("Home");

        for (int step = 0; step < 3; step++)
        {
            await user.Page.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
        }

        await user.Page.GetByText("Step 4 of 8: Polls").WaitForAsync();
        await Assertions.Expect(pollsAnchor).ToBeInViewportAsync();
        double pollsAnchorCenterX = await AnchorCenterXAsync(pollsAnchor);
        double pollsCardCenterX = await StableCardCenterXAsync(user.Page);
        await AssertNotClippedAsync("Polls");

        for (int step = 0; step < 4; step++)
        {
            await user.Page.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
        }

        await user.Page.GetByText("Step 8 of 8: Settings").WaitForAsync();
        await Assertions.Expect(settingsAnchor).ToBeInViewportAsync();
        double settingsAnchorCenterX = await AnchorCenterXAsync(settingsAnchor);
        double settingsCardCenterX = await StableCardCenterXAsync(user.Page);
        await AssertNotClippedAsync("Settings");

        // The card actually moved — catches a regression to a static (e.g. bottom-center) card,
        // which this same assertion at a single step could never distinguish from the real thing.
        Assert.True(
            Math.Abs(settingsCardCenterX - homeCardCenterX) > 40,
            $"Expected the card's horizontal center to move between distant steps; " +
            $"home={homeCardCenterX}, settings={settingsCardCenterX}.");

        AssertCardCenteredOnAnchor(homeCardCenterX, homeAnchorCenterX, innerWidth);
        AssertCardCenteredOnAnchor(pollsCardCenterX, pollsAnchorCenterX, innerWidth);
        AssertCardCenteredOnAnchor(settingsCardCenterX, settingsAnchorCenterX, innerWidth);

        // Dismiss so a leaked open tour never reaches a later fact sharing this account.
        IResponse response = await user.Page.RunAndWaitForResponseAsync(
            () => user.Page.GetByRole(AriaRole.Button, new() { Name = "Skip tour" }).ClickAsync(),
            candidate => candidate.Url.Contains("/api/v1/me/tour-completed", StringComparison.Ordinal) &&
                candidate.Request.Method == "POST");
        Assert.Equal(204, response.Status);
    }

    /// <summary>
    /// G1/G2: with a header anchor, the card's height (`CARD_MAX_HEIGHT_PX`, plans for 280px)
    /// exceeds this viewport's own height budget, and a naive "flip above when it doesn't fit
    /// below" would clamp the flipped position back down onto the very anchor it spotlights (the
    /// regression `computeCardPosition`'s guard now closes: it only flips when the above position
    /// genuinely clears the top margin on its own, otherwise it stays below-and-clamped). Proven
    /// geometrically — the card's box must never intersect the active anchor's box — not merely "is
    /// visible", which a card drawn on top of its own anchor would still satisfy.
    /// </summary>
    [Fact]
    public async Task CardPlacement_NeverDrawsOverItsOwnAnchorAtAShortLandscapeHeight_D182()
    {
        RegisteredUser user = await GetUserAsync();
        await user.Page.SetViewportSizeAsync(568, 320);
        await OpenViaReplayAsync(user.Page);

        ILocator homeAnchor = user.Page.Locator("[data-tour='home']:visible");
        await Assertions.Expect(homeAnchor).ToBeInViewportAsync();

        LocatorBoundingBoxResult? anchorBox = await homeAnchor.BoundingBoxAsync();
        Assert.NotNull(anchorBox);
        LocatorBoundingBoxResult? cardBox = await StableCardBoxAsync(user.Page);

        Assert.False(
            BoxesIntersect(anchorBox!, cardBox!),
            $"Tour card box {Describe(cardBox!)} must not intersect its own spotlit anchor " +
            $"{Describe(anchorBox!)} at a 568x320 viewport.");

        // Dismiss so a leaked open tour never reaches a later fact sharing this account.
        IResponse response = await user.Page.RunAndWaitForResponseAsync(
            () => user.Page.GetByRole(AriaRole.Button, new() { Name = "Skip tour" }).ClickAsync(),
            candidate => candidate.Url.Contains("/api/v1/me/tour-completed", StringComparison.Ordinal) &&
                candidate.Request.Method == "POST");
        Assert.Equal(204, response.Status);
        await user.Page.SetViewportSizeAsync(1280, 800);
    }

    private static async Task<double> AnchorCenterXAsync(ILocator anchor)
    {
        LocatorBoundingBoxResult? box = await anchor.BoundingBoxAsync();
        Assert.NotNull(box);
        return box!.X + box.Width / 2;
    }

    /// <summary>
    /// Samples the tour card's horizontal center, waiting for two consecutive identical samples so
    /// the read lands after `motion-safe:transition-[top,left]` finishes settling into its new
    /// position — a one-shot sample mid-transition would race it exactly the way the narrow-viewport
    /// scroll assertion above used to before it became an auto-retrying `ToBeInViewportAsync`.
    /// </summary>
    private static async Task<double> StableCardCenterXAsync(IPage page)
    {
        LocatorBoundingBoxResult box = await StableCardBoxAsync(page);
        return box.X + box.Width / 2;
    }

    /// <summary>
    /// Samples the tour card's full box, waiting for two consecutive identical samples (position
    /// AND size) so the read lands after `motion-safe:transition-[top,left]` finishes settling —
    /// the same race the narrow-viewport scroll assertion above used to hit before it became an
    /// auto-retrying `ToBeInViewportAsync`.
    /// </summary>
    private static async Task<LocatorBoundingBoxResult> StableCardBoxAsync(IPage page)
    {
        ILocator card = page.Locator("[data-testid='tour-overlay']");
        LocatorBoundingBoxResult? previous = null;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            LocatorBoundingBoxResult? box = await card.BoundingBoxAsync();
            Assert.NotNull(box);
            if (previous is { } value &&
                Math.Abs(box!.X - value.X) < 0.5 &&
                Math.Abs(box.Y - value.Y) < 0.5 &&
                Math.Abs(box.Width - value.Width) < 0.5 &&
                Math.Abs(box.Height - value.Height) < 0.5)
            {
                return box;
            }

            previous = box;
            await page.WaitForTimeoutAsync(25);
        }

        throw new TimeoutException("Tour card box did not settle.");
    }

    private static bool BoxesIntersect(LocatorBoundingBoxResult a, LocatorBoundingBoxResult b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width &&
        a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static string Describe(LocatorBoundingBoxResult box) =>
        $"{{X={box.X}, Y={box.Y}, Width={box.Width}, Height={box.Height}}}";

    /// <summary>
    /// Mirrors `computeCardPosition`'s clamped-center-under-anchor arithmetic (`TourOverlay.tsx`) —
    /// a tolerance-only check (e.g. "somewhere different") would pass for a card that moved to the
    /// wrong place just as easily as the right one; this asserts it landed at the exact place the
    /// component's own formula predicts for this anchor and viewport width.
    /// </summary>
    private static void AssertCardCenteredOnAnchor(double cardCenterX, double anchorCenterX, int innerWidth)
    {
        const int cardWidth = 320; // TourOverlay.tsx CARD_WIDTH_PX
        const int margin = 16; // TourOverlay.tsx CARD_MARGIN_PX
        double width = Math.Min(cardWidth, innerWidth - margin * 2);
        double maxLeft = Math.Max(innerWidth - width - margin, margin);
        double left = Math.Min(Math.Max(anchorCenterX - width / 2, margin), maxLeft);
        double expectedCenterX = left + width / 2;
        Assert.True(
            Math.Abs(cardCenterX - expectedCenterX) <= 3,
            $"Expected the card centered around {expectedCenterX} for anchor center {anchorCenterX} " +
            $"at innerWidth {innerWidth}, but it was at {cardCenterX}.");
    }
}
