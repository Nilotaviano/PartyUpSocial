using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Tour;

/// <summary>
/// TOUR_DESIGN.md §6: the tour's own browser coverage reaches the overlay through Replay — the
/// fixture skip (`PlaywrightFixture.RegisterAndOnboardAsync`) makes auto-open unreachable for lane
/// accounts by design, so every fact here registers once, dismisses the auto-tour via the fixture,
/// then walks: register → `/settings/account` → Replay → overlay.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
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
        await Assertions.Expect(user.Page.Locator("[data-tour='home']")).ToBeFocusedAsync();
    }

    /// <summary>
    /// TOUR_DESIGN.md §3: the nav scrolls horizontally on small screens (`App.tsx:64`) — the
    /// spotlight's scroll-into-view (not merely the ring) is what makes an off-screen anchor like
    /// Settings (the eighth stop) reachable at all at a narrow viewport.
    /// </summary>
    [Fact]
    public async Task Spotlight_ScrollsTheOffScreenAnchorIntoViewAtANarrowViewport_D182()
    {
        RegisteredUser user = await GetUserAsync();
        await user.Page.SetViewportSizeAsync(375, 667);
        await OpenViaReplayAsync(user.Page);

        ILocator settingsAnchor = user.Page.Locator("[data-tour='settings']");

        // Proves the nav genuinely overflows at this width — without this, a nav that happened to
        // fit everything on screen would pass the post-scroll assertion below for free, without the
        // spotlight's scroll-into-view doing any real work.
        await Assertions.Expect(settingsAnchor).Not.ToBeInViewportAsync();

        for (int step = 0; step < 7; step++)
        {
            await user.Page.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
        }

        await user.Page.GetByText("Step 8 of 8: Settings").WaitForAsync();
        // Ratio = 1 demands full containment and auto-retries, which also waits out the smooth
        // scroll animation — a one-shot BoundingBox sample here races it on slow runners.
        await Assertions.Expect(settingsAnchor).ToBeInViewportAsync(new() { Ratio = 1 });

        // Dismiss so a leaked open tour never reaches a later fact sharing this account.
        IResponse response = await user.Page.RunAndWaitForResponseAsync(
            () => user.Page.GetByRole(AriaRole.Button, new() { Name = "Skip tour" }).ClickAsync(),
            candidate => candidate.Url.Contains("/api/v1/me/tour-completed", StringComparison.Ordinal) &&
                candidate.Request.Method == "POST");
        Assert.Equal(204, response.Status);
        await user.Page.SetViewportSizeAsync(1280, 800);
    }
}
