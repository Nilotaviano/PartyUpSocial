using System.Globalization;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Sessions;

/// <summary>
/// PHASE4_DESIGN.md §11/§12 S6 row: real-browser proof of the registered session surface — create,
/// invite and respond, approve and promote, cancel, the dirty-form discard dialog, keyboard-only
/// RSVP, live status text, browser-zone time rendering, and the three shipped locales.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class SessionRegisteredFlowTests(PlaywrightFixture fixture)
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static Setup? _setup;

    private sealed record Setup(RegisteredUser Organizer, RegisteredUser Friend, RegisteredUser Stranger);

    /// <summary>
    /// Registration + onboarding + befriending is expensive (real mailbox round trips through
    /// multiple pages); it is done once per test run and shared across every fact in this class.
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

            RegisteredUser organizer = await fixture.RegisterAndOnboardAsync(
                await fixture.NewContextAsync(), "organizer", "Organizer One");
            RegisteredUser friend = await fixture.RegisterAndOnboardAsync(
                await fixture.NewContextAsync(), "friend", "Friend Two");
            RegisteredUser stranger = await fixture.RegisterAndOnboardAsync(
                await fixture.NewContextAsync(), "stranger", "Stranger Three");

            await PlaywrightFixture.BefriendAsync(organizer, friend);
            await PlaywrightFixture.BefriendAsync(organizer, stranger);

            _setup = new Setup(organizer, friend, stranger);
            return _setup;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    private static string StartTimeValue() =>
        DateTime.UtcNow.AddDays(1).Date.AddHours(18).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    private static async Task<string> CreateSessionAsync(
        IPage organizerPage,
        string title,
        RegisteredUser invitee,
        int capacity)
    {
        await organizerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");
        await organizerPage.Locator("#field-title").FillAsync(title);
        await organizerPage.Locator("#field-start").FillAsync(StartTimeValue());
        await organizerPage.Locator("#field-duration").FillAsync("60");
        await organizerPage.Locator("#field-min").FillAsync("1");
        await organizerPage.Locator("#field-max").FillAsync(capacity.ToString(CultureInfo.InvariantCulture));
        await organizerPage
            .GetByLabel($"{invitee.DisplayName} (@{invitee.Username})")
            .CheckAsync();
        await organizerPage.GetByRole(AriaRole.Button, new() { Name = "Create session" }).ClickAsync();
        await organizerPage.WaitForURLAsync(url =>
            System.Text.RegularExpressions.Regex.IsMatch(url, @"/sessions/[0-9a-fA-F-]{36}$"));
        return new Uri(organizerPage.Url).Segments[^1];
    }

    [Fact]
    public async Task CreateInviteRespondApproveAndPromote_MovesAWaitlistedGuestIntoTheRoster()
    {
        Setup setup = await GetSetupAsync();
        string title = "Raid night " + Guid.NewGuid().ToString("N")[..6];
        // The organizer occupies one Going slot from creation (D-155), so capacity 2 is the
        // smallest value that leaves exactly one free slot for the Friend to take.
        string sessionId = await CreateSessionAsync(setup.Organizer.Page, title, setup.Friend, capacity: 2);

        // Friend responds Going, taking the one slot free after the organizer's own (live text +
        // roster update).
        await setup.Friend.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        await setup.Friend.Page.GetByRole(AriaRole.Button, new() { Name = "Going", Exact = true }).ClickAsync();
        await setup.Friend.Page.GetByText("Your response was updated.").WaitForAsync();

        // Stranger (a friend of the organizer, so visibility grants read access to the public
        // session) requests to join a now-full session.
        await setup.Stranger.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        await setup.Stranger.Page.GetByRole(AriaRole.Button, new() { Name = "Request to join" }).ClickAsync();
        await setup.Stranger.Page.GetByText("Your join request was sent.").WaitForAsync();

        // Organizer approves; capacity is full so the approval lands the requester on the waitlist.
        await setup.Organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        await setup.Organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();
        await setup.Organizer.Page.GetByText("Join request approved.").WaitForAsync();
        await setup.Organizer.Page.GetByRole(AriaRole.Heading, new() { Name = "Waitlist (1)" }).WaitForAsync();

        // Friend leaves, freeing the one slot.
        await setup.Friend.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        await setup.Friend.Page.GetByRole(AriaRole.Button, new() { Name = "Leave session" }).ClickAsync();
        await setup.Friend.Page.GetByText("Your response was updated.").WaitForAsync();

        // Organizer promotes the waitlisted stranger into the roster.
        await setup.Organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        await setup.Organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Promote" }).ClickAsync();
        await setup.Organizer.Page.GetByText("Promoted from the waitlist.").WaitForAsync();
        // The waitlist section (conditionally rendered only while non-empty) disappears once the
        // sole waitlisted stranger is promoted into the roster.
        await Assertions.Expect(setup.Organizer.Page.GetByRole(AriaRole.Heading, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Waitlist") })).Not.ToBeVisibleAsync();

        // Organizer cancels — redirected to the upcoming list, proving the cancel action end to end.
        await setup.Organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Cancel session" }).First.ClickAsync();
        await setup.Organizer.Page
            .GetByRole(AriaRole.Dialog)
            .GetByRole(AriaRole.Button, new() { Name = "Cancel session" })
            .ClickAsync();
        await setup.Organizer.Page.WaitForURLAsync(url => url.EndsWith("/sessions/upcoming", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirtyCreateForm_DiscardDialog_CancelKeepsValuesDiscardLeaves()
    {
        Setup setup = await GetSetupAsync();
        IPage page = setup.Organizer.Page;
        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");

        await page.Locator("#field-title").FillAsync("Unsaved draft");
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).First.ClickAsync();

        ILocator dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByText("Discard unsaved changes?").WaitForAsync();

        // Cancel keeps the form open with the typed value intact.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#field-title")).ToHaveValueAsync("Unsaved draft");

        // Discard abandons the draft and leaves the page.
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).First.ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Discard" }).ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith("/sessions/upcoming", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeyboardOnlyRsvp_TogglesGoingWithoutAMouse()
    {
        Setup setup = await GetSetupAsync();
        string sessionId = await CreateSessionAsync(
            setup.Organizer.Page, "Keyboard proof " + Guid.NewGuid().ToString("N")[..6], setup.Friend, capacity: 4);

        IPage page = setup.Friend.Page;
        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        ILocator going = page.GetByRole(AriaRole.Button, new() { Name = "Going", Exact = true });
        await going.FocusAsync();
        await Assertions.Expect(going).ToBeFocusedAsync();
        await going.PressAsync("Enter");

        // The RSVP mutation's own success confirmation is the keyboard-activation proof: the
        // control was reached and actuated with focus + Enter alone, no pointer input anywhere
        // in this test.
        await page.GetByText("Your response was updated.").WaitForAsync();
    }

    [Fact]
    public async Task SessionStartTime_RendersInTheBrowserDetectedTimeZone_D169A()
    {
        Setup setup = await GetSetupAsync();
        DateTime start = DateTime.UtcNow.AddDays(2).Date.AddHours(20);
        IPage page = setup.Organizer.Page;
        await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");
        await page.Locator("#field-title").FillAsync("Timezone proof");
        await page.Locator("#field-start").FillAsync(start.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));
        await page.Locator("#field-duration").FillAsync("60");
        await page
            .GetByLabel($"{setup.Friend.DisplayName} (@{setup.Friend.Username})")
            .CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Create session" }).ClickAsync();
        await page.WaitForURLAsync(url =>
            System.Text.RegularExpressions.Regex.IsMatch(url, @"/sessions/[0-9a-fA-F-]{36}$"));

        // The `#field-start` input is a browser-local datetime-local control: the app reads its
        // literal wall-clock digits as the browser's own zone (`new Date(form.start)`), so the
        // round-tripped render reproduces the same wall-clock digits typed in, not `start`
        // reinterpreted through the test host's zone. The time-of-day is load-bearing here — a
        // date-only assertion is host-offset-independent and would pass under any rendered zone.
        string expected = start.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture);
        await page.GetByText(expected, new PageGetByTextOptions { Exact = false }).WaitForAsync();
    }

    [Theory]
    [InlineData("Português (Brasil)", "Sessões")]
    [InlineData("Español", "Sesiones")]
    public async Task SessionsNav_RendersInEachShippedLocale_D060(string optionLabel, string sessionsLabel)
    {
        // Reuses an already-registered account (register-ip is rate-limited to 5/hour per
        // A-073's `register-ip` policy, and this suite already spends its budget on the shared
        // organizer/friend/stranger setup) rather than registering a fresh one per locale.
        Setup setup = await GetSetupAsync();
        // A fresh tab in the stranger's already-authenticated context (same cookies), rather than
        // the same `Page` object another fact is mid-flow on.
        IPage page = await setup.Stranger.Page.Context.NewPageAsync();
        try
        {
            await PlaywrightFixture.SwitchLanguageAsync(page, optionLabel);
            await page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/app");

            await page.GetByRole(AriaRole.Link, new() { Name = sessionsLabel }).WaitForAsync();
        }
        finally
        {
            await PlaywrightFixture.SwitchLanguageAsync(page, "English");
            await page.CloseAsync();
        }
    }
}
