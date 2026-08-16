using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Engine;

/// <summary>
/// PHASE8_DESIGN.md §3 S8 (D-138, Q3): the liveness guard for the engine-parameterized lanes, the
/// firefox-lane analogue of <see cref="Availability.WebKitLaneSmokeTests"/>. The `browser` CI job now
/// runs three lanes off one fixture whose engine is chosen by `PARTYUP_BROWSER_ENGINE`, and the
/// failure mode that costs nothing to introduce and everything to notice is a lane that quietly
/// boots the default engine anyway — a misspelled variable name in `ci.yml`, a matrix key that never
/// reaches the step's `env:`, a runner that strips it. Nothing else in the suite would go red: every
/// flow test passes just as happily on Chromium.
/// <para>
/// So this class reads the engine back off the live <see cref="IBrowser"/> instance itself
/// (<c>Browser.BrowserType.Name</c>, Playwright's own report of what actually launched) and compares
/// it against what the environment asked for. It is deliberately engine-agnostic — it carries no
/// `Browser` trait, so the chromium lane discovers it too and proves that lane's default, and the
/// firefox lane names it in its FullyQualifiedName filter so the lane can never consist of flow
/// tests alone.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class BrowserEngineLaneTests(PlaywrightFixture fixture)
{
    [Fact]
    public void SharedBrowserRunsTheRequestedEngine_D138()
    {
        string expected = Environment.GetEnvironmentVariable("PARTYUP_BROWSER_ENGINE") is { Length: > 0 } requested
            ? requested.Trim().ToLowerInvariant()
            : "chromium";

        Assert.Equal(expected, PlaywrightFixture.EngineName);
        Assert.Equal(expected, fixture.Browser.BrowserType.Name);
    }

    /// <summary>
    /// The engine is not merely launched but actually drives the app the lane exists to exercise:
    /// the shipped SPA shell renders and routes under it. A lane whose flow filter matched nothing
    /// still runs this, so "the firefox lane was green" cannot mean "the firefox lane was empty".
    /// </summary>
    [Fact]
    public async Task RequestedEngineRendersAndRoutesTheAppShell_D138()
    {
        await using IBrowserContext context = await fixture.NewContextAsync();
        IPage page = await context.NewPageAsync();

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/welcome");
        // Not `.First` over all links: the shell's first link is the sr-only skip link,
        // which is invisible until focused and therefore never tappable/clickable here.
        ILocator continueLink = page.GetByRole(AriaRole.Link, new() { Name = "Create account" }).First;
        await continueLink.WaitForAsync();
        await continueLink.ClickAsync();
        await page.WaitForURLAsync(url => !url.EndsWith("/welcome", StringComparison.Ordinal));
    }
}
