using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Availability;

/// <summary>
/// A-082's S1-authored non-zero-count proof for the WebKit pointer step: `[Trait("Browser","WebKit")]`
/// is what routes a class to the `browser` CI job's second step (`--filter "Browser=WebKit"`), whose
/// complementary main-Chromium step filters `Category=Integration&Browser!=WebKit` — so this class
/// runs in exactly one of the two steps, never both. S6 adds the real
/// `AvailabilityCalendarPointerTests` under the same trait (`P5-TOUCH-01`) without touching
/// `ci.yml`; this class's only job is making sure that step's discovered count is never zero before
/// S6 exists. Launches its own <see cref="IPlaywright"/>/WebKit instance rather than reusing
/// <see cref="PlaywrightFixture.Browser"/> (Chromium) — a pointer-lane smoke test proving nothing
/// about WebKit specifically would defeat its own purpose.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Browser", "WebKit")]
[Collection(PlaywrightSuite.Name)]
public sealed class WebKitLaneSmokeTests
{
    // No PlaywrightFixture constructor parameter: this class drives its own WebKit instance and
    // only needs PlaywrightFixture.WebBaseUrl (static). [Collection] alone is enough to make xunit
    // start the shared fixture (and the API/web stack behind it) before this class runs.

    [Fact]
    public async Task WebKitLoadsThePageAndRegistersATouchTap_D139()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser webkit = await playwright.Webkit.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using IBrowserContext context = await webkit.NewContextAsync(new BrowserNewContextOptions
        {
            HasTouch = true,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        });
        IPage page = await context.NewPageAsync();

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/welcome");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        ILocator continueLink = page.GetByRole(AriaRole.Link).First;
        await continueLink.WaitForAsync();

        // A real touch tap (not a synthesized mouse click) registering at all is the pointer-lane
        // proof this smoke test exists for — `P5-TOUCH-01`'s real assertions (long-press,
        // drag-auto-scroll, scroll-conflict resolution) land with the real grid in S6.
        await continueLink.TapAsync();
        await page.WaitForURLAsync(url => !url.EndsWith("/welcome", StringComparison.Ordinal));
    }
}
