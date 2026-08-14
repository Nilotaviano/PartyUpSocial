using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Discover;

/// <summary>
/// PHASE5_DESIGN.md §11 "Browser — Chromium (S6/S8)": a real browser drives Discover end to end —
/// row → the shipped <c>/sessions/:id</c> detail route (masked roster + notes visible pre-join,
/// D-114) → a request-to-join mutation that succeeds (D-149).
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class DiscoverFlowTests(PlaywrightFixture fixture)
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static Setup? _setup;

    private sealed record Setup(
        RegisteredUser Organizer,
        RegisteredUser Extender,
        RegisteredUser Viewer,
        string SessionId,
        string Title,
        string Notes);

    private static string StartTimeValue() =>
        DateTime.UtcNow.AddDays(2).Date.AddHours(19).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// A-077 (c): Discover surfaces a session only through an unblocked inherited path — an
    /// unattached viewer with a friend who extends. The organizer and the viewer here share no
    /// direct friendship at all, only the shared Extender in the middle, so the row reaching the
    /// viewer's feed proves Discover's own rule, not a friends-list shortcut.
    /// </summary>
    private async Task<Setup> GetSetupAsync()
    {
        if (_setup is not null)
        {
            return _setup;
        }

        await SetupLock.WaitAsync();
        try
        {
            if (_setup is not null)
            {
                return _setup;
            }

            RegisteredUser organizer = await fixture.SeedRegisteredUserAsync(
                await fixture.NewContextAsync(), "discoverorganizer", "Discover Organizer");
            RegisteredUser extender = await fixture.SeedRegisteredUserAsync(
                await fixture.NewContextAsync(), "discoverextender", "Discover Extender");
            RegisteredUser viewer = await fixture.SeedRegisteredUserAsync(
                await fixture.NewContextAsync(), "discoverviewer", "Discover Viewer");

            await PlaywrightFixture.BefriendAsync(organizer, extender);
            await PlaywrightFixture.BefriendAsync(extender, viewer);

            string title = "Discover flow " + Guid.NewGuid().ToString("N")[..6];
            string notes = "Pre-join notes " + Guid.NewGuid().ToString("N")[..6];

            await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");
            await organizer.Page.Locator("#field-title").FillAsync(title);
            await organizer.Page.Locator("#field-start").FillAsync(StartTimeValue());
            await organizer.Page.Locator("#field-duration").FillAsync("60");
            await organizer.Page.Locator("#field-notes").FillAsync(notes);
            await organizer.Page.Locator("#field-min").FillAsync("1");
            await organizer.Page.Locator("#field-max").FillAsync("4");
            await organizer.Page
                .GetByLabel($"{extender.DisplayName} (@{extender.Username})")
                .CheckAsync();
            await organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Create session" }).ClickAsync();
            await organizer.Page.WaitForURLAsync(url => Regex.IsMatch(url, @"/sessions/[0-9a-fA-F-]{36}$"));
            string sessionId = new Uri(organizer.Page.Url).Segments[^1];

            // The extender must actually participate for A-077 (c)'s "friend who extends" rule to
            // grant the viewer an inherited path — this makes the extending act explicit and
            // observable rather than relying on the still-Pending invited-but-unanswered row.
            await extender.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
            await extender.Page.GetByRole(AriaRole.Button, new() { Name = "Going", Exact = true }).ClickAsync();
            await extender.Page.GetByText("Your response was updated.").WaitForAsync();

            _setup = new Setup(organizer, extender, viewer, sessionId, title, notes);
            return _setup;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    [Fact]
    public async Task DiscoverRowToDetailToJoinRequest_D149_D114()
    {
        Setup setup = await GetSetupAsync();
        IPage page = setup.Viewer.Page;

        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/discover");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        ILocator row = page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(Regex.Escape(setup.Title)) });
        await row.WaitForAsync();
        await row.ClickAsync();

        // The row links straight to the shipped session detail route (D-149) — no second Discover
        // detail surface.
        await page.WaitForURLAsync(url => url.EndsWith("/sessions/" + setup.SessionId, StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Heading, new() { Name = setup.Title }).WaitForAsync();

        // D-114: notes and the roster are visible pre-join to an unattached viewer with an inherited
        // path — the extender's own row renders by display name, never withheld until after joining.
        await page.GetByText(setup.Notes).WaitForAsync();
        await page.GetByText(setup.Extender.DisplayName).WaitForAsync();

        // The request-to-join mutation itself succeeds end to end.
        await page.GetByRole(AriaRole.Button, new() { Name = "Request to join" }).ClickAsync();
        await page.GetByText("Your join request was sent.").WaitForAsync();
    }
}
