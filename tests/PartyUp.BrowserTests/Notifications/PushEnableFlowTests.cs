using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Notifications;

/// <summary>
/// PHASE7_DESIGN.md §5/§6 (D-147/D-159/D-175, ruling 4): real-browser proof of the enable flow
/// through the spawned-process trigger seam. The lane is black-box, so the browser's real
/// <c>PushManager.subscribe</c> — which would otherwise need a live push service — is replaced by
/// a synthetic one via <see cref="IPage.AddInitScriptAsync(string)"/>; everything else (permission
/// grant, `sw.js` registration, the POST, the dispatch trigger, `LoggingPushSender`'s drop file) is
/// the real stack.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class PushEnableFlowTests(PlaywrightFixture fixture)
{
    /// <summary>
    /// Overrides <c>ServiceWorkerRegistration.prototype.pushManager</c> so the real
    /// `navigator.serviceWorker.register('/sw.js')` (a genuine registration against the real
    /// service worker this slice ships) exposes a fake <c>PushManager</c> whose <c>subscribe</c>
    /// resolves without ever reaching a real push service — the browser lane has none.
    /// </summary>
    private const string SyntheticPushManagerInitScript = """
        (() => {
            class FakePushSubscription {
                constructor() {
                    this.endpoint = 'https://push-lane.example/' + Math.random().toString(36).slice(2);
                }
                toJSON() {
                    return {
                        endpoint: this.endpoint,
                        keys: { p256dh: 'synthetic-p256dh-key', auth: 'synthetic-auth-secret' },
                    };
                }
                unsubscribe() {
                    return Promise.resolve(true);
                }
            }
            let currentSubscription = null;
            const fakeManager = {
                subscribe: () => {
                    currentSubscription = new FakePushSubscription();
                    return Promise.resolve(currentSubscription);
                },
                getSubscription: () => Promise.resolve(currentSubscription),
            };
            if (window.ServiceWorkerRegistration) {
                Object.defineProperty(window.ServiceWorkerRegistration.prototype, 'pushManager', {
                    configurable: true,
                    get() { return fakeManager; },
                });
            }
        })();
        """;

    private const string DeletePushManagerInitScript = "delete window.PushManager;";

    /// <summary>
    /// Wraps <c>Notification.requestPermission</c> with a call counter, read back via
    /// <c>window.__requestPermissionCalls</c> — proves the D-175 absent-<c>PushManager</c> arm
    /// never invokes it, rather than merely asserting the fallback UI appeared.
    /// </summary>
    private const string CountRequestPermissionInitScript = """
        (() => {
            window.__requestPermissionCalls = 0;
            if (window.Notification && window.Notification.requestPermission) {
                const original = window.Notification.requestPermission.bind(window.Notification);
                window.Notification.requestPermission = (...args) => {
                    window.__requestPermissionCalls++;
                    return original(...args);
                };
            }
        })();
        """;

    private static readonly string[] ExpectedDropFilePropertyNames =
        [.. new[] { "subscriptionId", "title", "body", "url", "tag" }.OrderBy(name => name, StringComparer.Ordinal)];

    private static string StartTimeValue() =>
        DateTime.UtcNow.AddDays(1).Date.AddHours(18).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// D-147 qualifying action ("creating a session") + D-099's invited-friend requirement in one
    /// step — mirrors <c>SessionRegisteredFlowTests.CreateSessionAsync</c>'s shipped idiom.
    /// `minAttendance=2` (not the shipped helper's `1`) is deliberate: with `1`, the organizer
    /// alone already meets minimum attendance and the session materializes `Confirmed` the
    /// instant it is created, dispatching its OWN push (tag-sharing with the invitation, ruling
    /// 2's intended tray-side grouping — correct product behavior, not a bug) that would collide
    /// with this test's single-notification assertion below. `2` keeps the session `Unconfirmed`
    /// until the invitee responds, so only `SessionInvitation` dispatches.
    /// </summary>
    private static async Task<string> CreateSessionInvitingAsync(
        IPage organizerPage,
        string title,
        RegisteredUser invitee)
    {
        await organizerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");
        await organizerPage.Locator("#field-title").FillAsync(title);
        await organizerPage.Locator("#field-start").FillAsync(StartTimeValue());
        await organizerPage.Locator("#field-duration").FillAsync("60");
        await organizerPage.Locator("#field-min").FillAsync("2");
        await organizerPage.Locator("#field-max").FillAsync("4");
        await organizerPage
            .GetByLabel($"{invitee.DisplayName} (@{invitee.Username})")
            .CheckAsync();
        await organizerPage.GetByRole(AriaRole.Button, new() { Name = "Create session" }).ClickAsync();
        await organizerPage.WaitForURLAsync(url => Regex.IsMatch(url, @"/sessions/[0-9a-fA-F-]{36}$"));
        return new Uri(organizerPage.Url).Segments[^1];
    }

    [Fact]
    public async Task EnableFlow_SubscribesAndDeliversOneDropFileWithNoCredential_D147_D159()
    {
        // The qualifying action must land on the SAME account that then enables push — a
        // second-device/account read of `pushPromptEligible` is exactly ruling 4's account-fact
        // axis, so this account first creates its own (throwaway) session before anything else.
        RegisteredUser recipient = await fixture.SeedRegisteredUserAsync(
            await fixture.NewContextAsync(), "pushrecipient", "Push Recipient");
        RegisteredUser sender = await fixture.SeedRegisteredUserAsync(
            await fixture.NewContextAsync(), "pushsender", "Push Sender");
        // `recipient.Page` is the one that clicks "Accept" inside `BefriendAsync` — reusing it
        // immediately afterward for a friends-list-dependent navigation would race the accept
        // mutation's own commit. The qualifying action below needs no friend at all (D-099 exempts
        // a private session), so it runs first, off `recipient`'s own page, with no such dependency;
        // only `sender`'s page (never touched since the initial friend-request step) reads the
        // now-settled friends list later, for the notification-producing session.
        await PlaywrightFixture.BefriendAsync(sender, recipient);

        // D-147 qualifying action ("creating a session") — private, so no invitee is required.
        await recipient.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");
        await recipient.Page.Locator("#field-title").FillAsync("Push lane self-qualifying session");
        await recipient.Page.Locator("#field-start").FillAsync(StartTimeValue());
        await recipient.Page.Locator("#field-duration").FillAsync("60");
        await recipient.Page.Locator("#field-min").FillAsync("1");
        await recipient.Page.Locator("#field-max").FillAsync("4");
        await recipient.Page.GetByLabel("Private session").CheckAsync();
        await recipient.Page.GetByRole(AriaRole.Button, new() { Name = "Create session" }).ClickAsync();
        await recipient.Page.WaitForURLAsync(url => Regex.IsMatch(url, @"/sessions/[0-9a-fA-F-]{36}$"));

        await recipient.Page.AddInitScriptAsync(SyntheticPushManagerInitScript);
        await recipient.Page.Context.GrantPermissionsAsync(
            ["notifications"],
            new BrowserContextGrantPermissionsOptions { Origin = PlaywrightFixture.WebBaseUrl });

        // A real navigation (not client-side routing) forces a fresh mount, so the profile query
        // re-fetches `pushPromptEligible` rather than serving the pre-qualification cached value.
        await recipient.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/app");

        await recipient.Page.GetByTestId("push-enable-card").WaitForAsync();
        await recipient.Page
            .GetByRole(AriaRole.Button, new() { Name = "Enable notifications" })
            .ClickAsync();

        // Card gone (asked + subscribed) proves the whole client-side chain — permission grant,
        // `sw.js` registration, the synthetic `subscribe`, and the POST — completed successfully.
        await recipient.Page.GetByTestId("push-enable-card").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached });

        int subscriptionCount = 0;
        DateTime subscriptionDeadline = DateTime.UtcNow.AddSeconds(15);
        while (subscriptionCount == 0 && DateTime.UtcNow < subscriptionDeadline)
        {
            subscriptionCount =
                await fixture.CountActivePushSubscriptionsForUsernameAsync(recipient.Username);
            if (subscriptionCount == 0)
            {
                await Task.Delay(250);
            }
        }
        Assert.Equal(1, subscriptionCount);

        // Trigger a real notification-producing action against the now-subscribed recipient — a
        // second session, invited after the subscription exists, so the dispatch trigger's next
        // 2s tick (`PlaywrightFixture`'s `Push:DispatchIntervalSeconds` override) has a live
        // subscription to fan out to.
        DateTime since = DateTime.UtcNow;
        string sessionId = await CreateSessionInvitingAsync(
            sender.Page, "Push lane notification-producing session", recipient);
        string expectedTag = $"session:{sessionId}";

        // `since` is captured before this call, but `BefriendAsync` above already sent
        // `recipient` a `friendRequestReceived` notification whose OWN dispatch can legitimately
        // land after `since` (dispatch is async, on the trigger seam's own 2s cadence) — so this
        // polls for the specific `tag` this test cares about rather than asserting the directory
        // holds exactly one file overall.
        string[] dropFiles = await fixture.WaitForPushDropFilesAsync(since, minimumCount: 1);
        string[] matchingTag = FilterByTag(dropFiles, expectedTag);
        DateTime dropDeadline = DateTime.UtcNow.AddSeconds(20);
        while (matchingTag.Length == 0 && DateTime.UtcNow < dropDeadline)
        {
            try
            {
                dropFiles = await fixture.WaitForPushDropFilesAsync(
                    since, minimumCount: dropFiles.Length + 1, timeout: TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // No further drop file appeared within this poll slice — loop condition re-checks
                // the overall deadline below.
            }
            matchingTag = FilterByTag(dropFiles, expectedTag);
        }

        Assert.Single(matchingTag);

        using JsonDocument document = JsonDocument.Parse(matchingTag[0]);
        JsonElement root = document.RootElement;

        Assert.Equal(expectedTag, root.GetProperty("tag").GetString());
        Assert.Equal($"/sessions/{sessionId}", root.GetProperty("url").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));

        // §3's drop-file contract: only {subscriptionId, title, body, url, tag} ever leave
        // `LoggingPushSender` — the raw endpoint/keys A-015 forbids logging are structurally
        // absent, not merely empty, so this enumerates the JSON's own top-level property names
        // rather than probing for specific forbidden keys (a fifth, unnamed credential field
        // would also fail this).
        string[] propertyNames = [.. root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)];
        Assert.Equal(ExpectedDropFilePropertyNames, propertyNames);
    }

    private static string[] FilterByTag(string[] dropFiles, string expectedTag) =>
        [.. dropFiles.Where(content =>
        {
            using JsonDocument document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("tag", out JsonElement tag)
                && tag.GetString() == expectedTag;
        })];

    [Fact]
    public async Task EnableFlow_AbsentPushManagerShowsTheD175FallbackNotice_AndNeverPrompts_D175()
    {
        RegisteredUser user = await fixture.SeedRegisteredUserAsync(
            await fixture.NewContextAsync(), "pushfallback", "Push Fallback");

        // Qualifying action, same as the happy-path arm above.
        await user.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");
        await user.Page.Locator("#field-title").FillAsync("Push fallback qualifying session");
        await user.Page.Locator("#field-start").FillAsync(StartTimeValue());
        await user.Page.Locator("#field-duration").FillAsync("60");
        await user.Page.Locator("#field-min").FillAsync("1");
        await user.Page.Locator("#field-max").FillAsync("4");
        await user.Page.GetByLabel("Private session").CheckAsync();
        await user.Page.GetByRole(AriaRole.Button, new() { Name = "Create session" }).ClickAsync();
        await user.Page.WaitForURLAsync(url => Regex.IsMatch(url, @"/sessions/[0-9a-fA-F-]{36}$"));

        // D-175: an absent `PushManager` — deleted before the SPA's own scripts run — routes
        // straight to the fallback notice; `Notification.requestPermission` is never invoked at
        // all (no context permission grant here — a real prompt would hang this test if the code
        // under test ever tried to invoke it).
        await user.Page.AddInitScriptAsync(CountRequestPermissionInitScript);
        await user.Page.AddInitScriptAsync(DeletePushManagerInitScript);
        await user.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/app");

        await user.Page
            .GetByText("This browser can't show push notifications.", new() { Exact = false })
            .WaitForAsync();
        Assert.Equal(0, await user.Page.GetByTestId("push-enable-card").CountAsync());
        Assert.Equal(0, await user.Page.EvaluateAsync<int>("window.__requestPermissionCalls"));

        // The notification center itself remains reachable — D-175's "center + badge are the
        // whole channel" for a browser this flow cannot subscribe.
        await user.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/notifications");
        await user.Page.GetByRole(AriaRole.Heading, new() { Name = "Notifications" }).WaitForAsync();
    }
}
