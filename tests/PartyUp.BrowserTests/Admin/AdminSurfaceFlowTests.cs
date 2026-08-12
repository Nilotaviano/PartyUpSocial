using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Admin;

/// <summary>
/// PHASE8_DESIGN.md §3 S3's browser-lane flow: the admin surface is URL-only (§2 Q2, ratified
/// 2026-08-12 — no navigation entry renders for anyone) and fails closed exactly the way D-177's
/// server side does. Both legs run in one fact against one account, in order: the account first
/// proves the no-leak posture without the role, then the fixture grants the role over its own
/// connection and the same account reaches the real pages. Splitting them would need a second
/// registration (each pays the fixture's 65-second shared-rate-window barrier) and would depend on
/// xUnit fact ordering, which is not guaranteed.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class AdminSurfaceFlowTests(PlaywrightFixture fixture)
{
    private const string NotFoundCopy = "This page is not available.";

    private static async Task AssertFailsClosedAsync(IPage page)
    {
        ILocator alert = page.GetByRole(AriaRole.Alert);
        await alert.WaitForAsync();
        Assert.Equal(NotFoundCopy, (await alert.InnerTextAsync()).Trim());
    }

    [Fact]
    public async Task AdminSurface_LeaksNoNavEntryAndFailsClosedUntilTheRoleIsGranted_D177_D178()
    {
        RegisteredUser user = await fixture.RegisterAndOnboardAsync(
            await fixture.NewContextAsync(),
            "adminflow",
            // Never a display name whose tokens include "admin": `all.txt` holds it as an exact
            // token term, so `ProfanityFilter` rejects the draft and onboarding never completes.
            "Adminflow Lane");
        IPage page = user.Page;

        // Leg 1, no role: nothing anywhere in the app chrome points at the surface, and both
        // routes answer with the page-local fail-closed branch — the same 404 posture the server
        // takes, saying only that the page does not exist.
        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/app");
        Assert.Equal(0, await page.Locator("a[href^='/admin']").CountAsync());
        Assert.DoesNotContain(
            "Usage dashboard",
            await page.Locator("body").InnerTextAsync(),
            StringComparison.Ordinal);

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/admin");
        await AssertFailsClosedAsync(page);
        Assert.Equal(0, await page.GetByRole(
            AriaRole.Heading,
            new() { Name = "Usage dashboard" }).CountAsync());

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/admin/users");
        await AssertFailsClosedAsync(page);

        // Leg 2: the role is resolved per request from the database, so the grant needs no
        // re-login — only a reload, so the SPA's cached queries refetch under it.
        Guid userId = await fixture.GrantAdminRoleAsync(user.Username);
        await page.ReloadAsync();

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/admin");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Usage dashboard" }).WaitForAsync();
        await page.GetByRole(AriaRole.Region, new() { Name = "Last 7 days" }).WaitForAsync();
        await page.GetByRole(AriaRole.Region, new() { Name = "Last 30 days" }).WaitForAsync();

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/admin/users");
        await page.GetByRole(AriaRole.Heading, new() { Name = "User management" }).WaitForAsync();

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/admin/users/" + userId);
        await page.GetByRole(AriaRole.Button, new() { Name = "Ban this account" }).WaitForAsync();
        // D-180: the destructive content removal is its own action, never an option inside the
        // plain ban confirmation.
        await page.GetByRole(AriaRole.Button, new() { Name = "Ban and remove content" })
            .WaitForAsync();

        // Q2 again, now WITH the role: an admin still gets no navigation entry — the surface stays
        // URL-only for everyone, which is what makes the 404 posture leak-proof.
        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/app");
        Assert.Equal(0, await page.Locator("a[href^='/admin']").CountAsync());
    }
}
