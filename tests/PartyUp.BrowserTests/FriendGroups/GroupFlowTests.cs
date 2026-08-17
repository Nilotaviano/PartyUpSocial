using System.Globalization;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.FriendGroups;

/// <summary>
/// Task 12 closing coverage: the friend-group create → invite → accept → target flow, driven
/// end to end through the real browser UI, proving the D-198 rule-2g grant and the D-206
/// provenance tag reach the actual pages (not just the API/domain layers Tasks 2-9 covered).
///
/// The plan's "via-tag on the list" language names a surface the shipped UI does not have —
/// `ShareTag` only renders on `SessionDetailPage` (`SessionListPage` carries no provenance tag
/// at all, see `web/src/features/sessions/lovable/SessionListPage.tsx`). This test asserts the
/// tag where it actually renders (the member's session detail page) and asserts the list-level
/// half of D-198/D-206 the way the shipped app expresses it: the session shows up in the
/// member's own upcoming list, and a stranger who is not in the group cannot reach it at all
/// (fails closed to the same redirect a nonexistent session gets, D-198).
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class GroupFlowTests(PlaywrightFixture fixture)
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static Setup? _setup;

    private sealed record Setup(RegisteredUser Owner, RegisteredUser Friend, RegisteredUser Stranger);

    /// <summary>
    /// Registration + onboarding + befriending is expensive (real mailbox round trips through
    /// multiple pages); it is done once per test run and shared across every fact in this class.
    /// The owner-friend pair is befriended (required for the group invite, D-200); the stranger
    /// is deliberately left unfriended and never invited to the group.
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

            RegisteredUser owner = await fixture.SeedRegisteredUserAsync(
                await fixture.NewContextAsync(), "grpowner", "Group Owner");
            RegisteredUser friend = await fixture.SeedRegisteredUserAsync(
                await fixture.NewContextAsync(), "grpfriend", "Group Friend");
            RegisteredUser stranger = await fixture.SeedRegisteredUserAsync(
                await fixture.NewContextAsync(), "grpstranger", "Group Stranger");

            await PlaywrightFixture.BefriendAsync(owner, friend);

            _setup = new Setup(owner, friend, stranger);
            return _setup;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    private static string StartTimeValue() =>
        DateTime.UtcNow.AddDays(1).Date.AddHours(19).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    [Fact]
    public async Task CreateGroupInviteAcceptAndTarget_ShowsViaTagToMembers_D198_D206()
    {
        Setup setup = await GetSetupAsync();
        string groupName = "Raid crew " + Guid.NewGuid().ToString("N")[..6];

        // Owner creates the group via the UI and invites the (already-friended) Friend.
        IPage ownerPage = setup.Owner.Page;
        await ownerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/groups");
        await ownerPage.GetByRole(AriaRole.Button, new() { Name = "New group" }).First.ClickAsync();

        ILocator newGroupDialog = ownerPage.GetByRole(AriaRole.Dialog);
        await newGroupDialog.Locator("#new-group-name").FillAsync(groupName);
        await newGroupDialog.GetByText(setup.Friend.DisplayName).ClickAsync();
        await newGroupDialog.GetByRole(AriaRole.Button, new() { Name = "Create group" }).ClickAsync();
        await Assertions.Expect(newGroupDialog).Not.ToBeVisibleAsync();

        await ownerPage.GetByText(groupName).WaitForAsync();

        // Friend accepts the pending invite from the Groups list page.
        IPage friendPage = setup.Friend.Page;
        await friendPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/groups");
        await friendPage.GetByText("Pending invites").WaitForAsync();
        await friendPage.GetByRole(AriaRole.Button, new() { Name = "Accept" }).ClickAsync();
        await friendPage.GetByText(groupName).WaitForAsync();

        // Owner creates a Groups-visibility session targeting the group (no direct invitees
        // needed at all under D-201 — only the audience group is required).
        string title = "Group session " + Guid.NewGuid().ToString("N")[..6];
        await ownerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");
        await ownerPage.Locator("#field-title").FillAsync(title);
        await ownerPage.Locator("#field-start").FillAsync(StartTimeValue());
        await ownerPage.Locator("#field-duration").FillAsync("60");
        await ownerPage.GetByRole(AriaRole.Radio, new() { Name = "Groups" }).ClickAsync();
        await ownerPage.GetByRole(AriaRole.Button, new() { Name = groupName, Exact = false }).ClickAsync();
        await ownerPage.GetByRole(AriaRole.Button, new() { Name = "Create session" }).ClickAsync();
        await ownerPage.WaitForURLAsync(url =>
            System.Text.RegularExpressions.Regex.IsMatch(url, @"/sessions/[0-9a-fA-F-]{36}$"));
        string sessionId = new Uri(ownerPage.Url).Segments[^1];

        // Friend (an Accepted member) sees the session on their own upcoming list, and the
        // detail page carries the named D-206 provenance tag.
        await friendPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/upcoming");
        await friendPage.GetByText(title).WaitForAsync();

        await friendPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        await friendPage.GetByText("via " + groupName).WaitForAsync();

        // A stranger (never invited to the group, no relationship to the owner) sees no such
        // session on their own list, and a direct visit fails closed exactly like a nonexistent
        // session (D-198 rule-2g: no membership, no access).
        IPage strangerPage = setup.Stranger.Page;
        await strangerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/upcoming");
        // Wait for the list to actually finish loading (the stranger has no friendships and no
        // group membership, so their own upcoming list is genuinely empty) before asserting the
        // title is absent — otherwise the assertion can pass vacuously while the fetch is still
        // in flight.
        await strangerPage.GetByText("No upcoming sessions.").WaitForAsync();
        await Assertions.Expect(strangerPage.GetByText(title)).Not.ToBeVisibleAsync();

        await strangerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        await strangerPage.WaitForURLAsync(url => url.EndsWith("/welcome", StringComparison.Ordinal));
    }
}
