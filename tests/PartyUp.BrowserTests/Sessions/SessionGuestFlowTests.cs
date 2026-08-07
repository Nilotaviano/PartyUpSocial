using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Sessions;

/// <summary>
/// PHASE4_DESIGN.md §11/§12 S6 row: real-browser proof of the guest bearer-link surface — the
/// 302/cookie/tokenless exchange, claim, rename, multi-device last-write-wins, and the
/// revoked/canceled terminal notices. Every assertion below checks the URL carries no token once
/// the exchange has happened (A-064/A-065).
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class SessionGuestFlowTests(PlaywrightFixture fixture)
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
                await fixture.NewContextAsync(), "guestorganizer", "Guest Organizer");
            return _organizer;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    private static string StartTimeValue(DateTime startUtc) =>
        startUtc.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// A private session needs no invited registered friend (D-099 only gates non-private
    /// creation), which keeps the guest-only scenarios below to a single registered account.
    /// </summary>
    /// <remarks>
    /// Round-1 F2: this used to click "Create session" and then <c>WaitForURLAsync</c> for the
    /// client's own post-create redirect. <c>SessionCreatePage.tsx</c>'s <c>onSuccess</c> handler
    /// <c>await</c>s <c>queryClient.invalidateQueries({queryKey:['sessions']})</c> <b>before</b>
    /// calling <c>navigate()</c>, so that client-side redirect is gated on a background refetch
    /// whose duration scales with whatever else is loading the API at the time — under a full
    /// lane run (this class plus the axe pass plus the registered-flow class, one shared fixture)
    /// that occasionally pushed well past even a 60s wait, which is what made
    /// <c>SecondDeviceEditWins_D126</c> nondeterministic. A longer wait cannot fix a delay with no
    /// fixed bound, so the barrier here is the POST's own response instead: the created session id
    /// comes straight off <c>CreateSessionResponse.session.id</c>, and this method navigates the
    /// page there itself rather than depending on the client ever getting around to it. This does
    /// not weaken what the class asserts — no fact here targets the post-create redirect itself
    /// (that path stays covered, unchanged, by <c>SessionRegisteredFlowTests.CreateSessionAsync</c>);
    /// every fact below still exercises the same private-session-then-guest-link setup it did before.
    /// </remarks>
    private static async Task<string> CreatePrivateSessionAsync(IPage organizerPage, string title, DateTime startUtc)
    {
        await organizerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");
        await organizerPage.Locator("#field-title").FillAsync(title);
        await organizerPage.Locator("#field-start").FillAsync(StartTimeValue(startUtc));
        await organizerPage.Locator("#field-duration").FillAsync("60");
        await organizerPage.Locator("#field-min").FillAsync("1");
        await organizerPage.Locator("#field-max").FillAsync("4");
        await organizerPage.GetByLabel("Private session").CheckAsync();

        IResponse response = await organizerPage.RunAndWaitForResponseAsync(
            () => organizerPage.GetByRole(AriaRole.Button, new() { Name = "Create session" }).ClickAsync(),
            candidate => candidate.Url.Contains("/api/v1/sessions", StringComparison.Ordinal) &&
                candidate.Request.Method == "POST");
        Assert.Equal(201, response.Status);
        JsonElement body = (await response.JsonAsync())!.Value;
        string sessionId = body.GetProperty("session").GetProperty("id").GetString()!;

        await organizerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        return sessionId;
    }

    private static async Task<string> CreateGuestLinkAsync(IPage organizerPage, string sessionId)
    {
        await organizerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId + "/invite");
        await organizerPage.Locator("#guest-link-count").FillAsync("1");
        await organizerPage.GetByRole(AriaRole.Button, new() { Name = "Create guest link" }).ClickAsync();
        return await organizerPage.Locator("span.font-mono").First.InnerTextAsync();
    }

    /// <summary>
    /// Exchanges the bearer link in a fresh browser context (a new device/tab) and asserts the
    /// A-064/A-065 contract: the token is gone from the address bar once the redirect lands.
    /// </summary>
    private async Task<IPage> ExchangeGuestLinkAsync(string guestLinkUrl, string? timezoneId = null)
    {
        IBrowserContext context = await fixture.NewContextAsync(timezoneId);
        IPage page = await context.NewPageAsync();
        await page.GotoAsync(guestLinkUrl);

        Assert.DoesNotContain("/g/", page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("token", page.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/guest/", page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(GuestLinkToken(guestLinkUrl), page.Url, StringComparison.Ordinal);

        IReadOnlyList<BrowserContextCookiesResult> cookies = await context.CookiesAsync();
        Assert.Contains(cookies, cookie => cookie.Name.StartsWith("pu.g.", StringComparison.Ordinal));
        return page;
    }

    /// <summary>The bearer secret is the last path segment of the harvested `/g/&lt;token&gt;` link.</summary>
    private static string GuestLinkToken(string guestLinkUrl) =>
        new Uri(guestLinkUrl).Segments[^1].TrimEnd('/');

    [Fact]
    public async Task ClaimNeedsOnlyADisplayName_D017()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string sessionId = await CreatePrivateSessionAsync(
            organizer.Page, "Claim proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(1));
        string url = await CreateGuestLinkAsync(organizer.Page, sessionId);

        IPage guestPage = await ExchangeGuestLinkAsync(url);
        await guestPage.GetByLabel("Display name").FillAsync("Casual Casey");
        // No RSVP option is selected — claim must still succeed with only the name.
        await guestPage.GetByRole(AriaRole.Button, new() { Name = "Respond" }).ClickAsync();

        await guestPage.GetByText("Your response was updated.").WaitForAsync();
        await Assertions.Expect(guestPage.GetByLabel("Display name")).ToHaveValueAsync("Casual Casey");
        Assert.DoesNotContain("token", guestPage.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecondDeviceEditWins_D126()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string sessionId = await CreatePrivateSessionAsync(
            organizer.Page, "Multi-device proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(1));
        string url = await CreateGuestLinkAsync(organizer.Page, sessionId);

        IPage deviceA = await ExchangeGuestLinkAsync(url);
        await deviceA.GetByLabel("Display name").FillAsync("Device A Name");
        await deviceA.GetByRole(AriaRole.Button, new() { Name = "Respond" }).ClickAsync();
        await deviceA.GetByText("Your response was updated.").WaitForAsync();

        // A second device exchanges the *same* link (design §9: multiple exchanges of one link
        // remain independently usable) and renames the shared guest identity.
        IPage deviceB = await ExchangeGuestLinkAsync(url);
        await deviceB.GetByLabel("Display name").WaitForAsync();
        await Assertions.Expect(deviceB.GetByLabel("Display name")).ToHaveValueAsync("Device A Name");
        await deviceB.GetByLabel("Display name").FillAsync("Device B Wins");
        await deviceB.GetByRole(AriaRole.Button, new() { Name = "Respond" }).ClickAsync();
        await deviceB.GetByText("Your response was updated.").WaitForAsync();

        await deviceA.ReloadAsync();
        await Assertions.Expect(deviceA.GetByLabel("Display name")).ToHaveValueAsync("Device B Wins");
    }

    [Fact]
    public async Task RemoveGuest_RedirectsToTokenlessTerminalView_D044()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string sessionId = await CreatePrivateSessionAsync(
            organizer.Page, "Removal proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(1));
        string url = await CreateGuestLinkAsync(organizer.Page, sessionId);

        IPage guestPage = await ExchangeGuestLinkAsync(url);
        string tokenlessUrl = guestPage.Url;
        await guestPage.GetByLabel("Display name").FillAsync("Removable Guest");
        await guestPage.GetByRole(AriaRole.Button, new() { Name = "Respond" }).ClickAsync();
        await guestPage.GetByText("Your response was updated.").WaitForAsync();

        await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        await organizer.Page
            .GetByRole(AriaRole.Button, new() { Name = "Remove guest Removable Guest" })
            .ClickAsync();
        await organizer.Page
            .GetByRole(AriaRole.Dialog)
            .GetByRole(AriaRole.Button, new() { Name = "Remove guest" })
            .ClickAsync();
        // "Remove guest" text is ambiguous here (roster button, dialog title, and the success
        // status all render the same string), so the discriminating post-mutation signal is the
        // guest's own remove button leaving the roster — that requires the write to have landed
        // and the refetch to have completed before the guest page is revisited.
        await Assertions.Expect(organizer.Page
                .GetByRole(AriaRole.Button, new() { Name = "Remove guest Removable Guest" }))
            .ToBeHiddenAsync();

        // Revisiting the exact same tokenless URL (no `/g/{token}` round trip) now shows the
        // terminal notice — the removal revoked the credential's underlying link.
        await guestPage.GotoAsync(tokenlessUrl);
        await guestPage.GetByText("This link was revoked.").WaitForAsync();
        Assert.DoesNotContain("token", guestPage.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/g/", guestPage.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(GuestLinkToken(url), guestPage.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanceledSessionRevokesTheLink_D174()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string sessionId = await CreatePrivateSessionAsync(
            organizer.Page, "Cancel proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(1));
        string url = await CreateGuestLinkAsync(organizer.Page, sessionId);
        IPage guestPage = await ExchangeGuestLinkAsync(url);
        string tokenlessUrl = guestPage.Url;

        await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        await organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Cancel session" }).First.ClickAsync();
        await organizer.Page
            .GetByRole(AriaRole.Dialog)
            .GetByRole(AriaRole.Button, new() { Name = "Cancel session" })
            .ClickAsync();
        await organizer.Page.WaitForURLAsync(url2 => url2.EndsWith("/sessions/upcoming", StringComparison.Ordinal));

        // The guest never received anything (D-167: guests are never notification recipients —
        // there is no channel to reach an anonymous guest identity at all); they only learn the
        // session is gone by revisiting their own link.
        await guestPage.GotoAsync(tokenlessUrl);
        await guestPage.GetByText("This session was canceled.").WaitForAsync();
        Assert.DoesNotContain("token", guestPage.Url, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Same scenario as <see cref="CanceledSessionRevokesTheLink_D174"/>, named for D-167's own
    /// citation: the guest's only signal is what they see on revisiting the link — no separate
    /// notification path exists for an identity with no channel to notify.
    /// </summary>
    [Fact]
    public async Task GuestSeesCancellationWithoutAnyNotification_D167()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string sessionId = await CreatePrivateSessionAsync(
            organizer.Page, "No-notify proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(1));
        string url = await CreateGuestLinkAsync(organizer.Page, sessionId);
        IPage guestPage = await ExchangeGuestLinkAsync(url);
        string tokenlessUrl = guestPage.Url;
        await guestPage.GetByLabel("Display name").FillAsync("Quiet Guest");
        await guestPage.GetByRole(AriaRole.Button, new() { Name = "Respond" }).ClickAsync();
        await guestPage.GetByText("Your response was updated.").WaitForAsync();

        await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        await organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Cancel session" }).First.ClickAsync();
        await organizer.Page
            .GetByRole(AriaRole.Dialog)
            .GetByRole(AriaRole.Button, new() { Name = "Cancel session" })
            .ClickAsync();
        await organizer.Page.WaitForURLAsync(url2 => url2.EndsWith("/sessions/upcoming", StringComparison.Ordinal));

        await guestPage.GotoAsync(tokenlessUrl);
        await guestPage.GetByText("This session was canceled.").WaitForAsync();
    }

    /// <summary>
    /// D-169: the guest view formats the start time in the *browser's* zone, never a server- or
    /// profile-supplied one. Two guest contexts pinned to different Playwright TimezoneIds render
    /// the same session: the UTC context's render is parsed to recover the instant, and the
    /// Honolulu context must then show that instant shifted by its own offset. The proof needs no
    /// knowledge of how the organizer's create-form input was interpreted server-side, and fails
    /// if the page substitutes any fixed or server-supplied zone for the browser's.
    /// </summary>
    [Fact]
    public async Task TimesRenderInTheBrowserZone_D169()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string sessionId = await CreatePrivateSessionAsync(
            organizer.Page, "Zone proof " + Guid.NewGuid().ToString("N")[..6],
            DateTime.UtcNow.AddDays(1).Date.AddHours(1));
        string url = await CreateGuestLinkAsync(organizer.Page, sessionId);

        string utcRender = await RenderedStartTimeAsync(url, "Etc/UTC");
        DateTime instantUtc = DateTime.SpecifyKind(
            DateTime.ParseExact(utcRender, "MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture),
            DateTimeKind.Utc);

        string honoluluRender = await RenderedStartTimeAsync(url, "Pacific/Honolulu");
        DateTime expected = TimeZoneInfo.ConvertTimeFromUtc(
            instantUtc, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Honolulu"));

        Assert.Equal(
            expected.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture),
            honoluluRender);
        Assert.NotEqual(utcRender, honoluluRender);
    }

    private async Task<string> RenderedStartTimeAsync(string guestLinkUrl, string timezoneId)
    {
        IPage page = await ExchangeGuestLinkAsync(guestLinkUrl, timezoneId);
        await page.GetByText("Times are shown in your device's time zone.").WaitForAsync();
        string pageText = await page.Locator("[data-testid=screen-guest-session]").InnerTextAsync();
        string normalized = Regex.Replace(pageText, @"\s+", " ");
        Match match = Regex.Match(normalized, @"\w{3} \d{1,2}, \d{4}, \d{1,2}:\d{2} [AP]M");
        Assert.True(match.Success, "No rendered start time found in: " + normalized);
        return match.Value;
    }
}
