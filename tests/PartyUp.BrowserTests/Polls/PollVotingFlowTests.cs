using System.Globalization;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Polls;

/// <summary>
/// PHASE6_DESIGN.md §5/§6 S6 row: real-browser proof of the voting grid's APG radiogroup idiom
/// (roving tabindex, arrow-key traversal, focus retention on revise — the S6 gate), the layout
/// shape of a vote row, and the guest claim-then-vote flow over the poll-typed guest link.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class PollVotingFlowTests(PlaywrightFixture fixture)
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
                await fixture.NewContextAsync(), "pollvoter", "Poll Voter Organizer");
            return _organizer;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    private static string OptionTimeValue(DateTime startUtc) =>
        startUtc.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Drives the real `/polls/new` create form (D-125 exempts a private poll from needing an
    /// invited friend) with two candidate times. Mirrors
    /// `SessionGuestFlowTests.CreatePrivateSessionAsync`'s barrier choice: the create POST's own
    /// response is the barrier (asserted 201), never the client's post-`invalidateQueries` redirect
    /// — a 4xx there would otherwise surface as a silent 30s navigation timeout instead of a loud
    /// assert. The created poll id comes straight off `CreatePollResponse.poll.id`.
    /// </summary>
    private static async Task<string> CreatePrivatePollAsync(IPage organizerPage, string title, DateTime firstOptionUtc)
    {
        await organizerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/new");
        await organizerPage.Locator("#field-title").FillAsync(title);
        await organizerPage.Locator("#field-option-value").FillAsync(OptionTimeValue(firstOptionUtc));
        await organizerPage.GetByRole(AriaRole.Button, new() { Name = "Add time" }).ClickAsync();
        await organizerPage.Locator("#field-option-value").FillAsync(OptionTimeValue(firstOptionUtc.AddDays(1)));
        await organizerPage.GetByRole(AriaRole.Button, new() { Name = "Add time" }).ClickAsync();
        await organizerPage.Locator("#field-min").FillAsync("1");
        await organizerPage.Locator("#field-max").FillAsync("4");
        await organizerPage.GetByLabel("Private poll").CheckAsync();

        IResponse response = await organizerPage.RunAndWaitForResponseAsync(
            () => organizerPage.GetByRole(AriaRole.Button, new() { Name = "Create poll" }).ClickAsync(),
            candidate => candidate.Url.Contains("/api/v1/polls", StringComparison.Ordinal) &&
                candidate.Request.Method == "POST");
        Assert.Equal(201, response.Status);
        System.Text.Json.JsonElement body = (await response.JsonAsync())!.Value;
        string pollId = body.GetProperty("poll").GetProperty("id").GetString()!;

        await organizerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/" + pollId);
        return pollId;
    }

    private static async Task<string> MintPollGuestLinkAsync(IPage organizerPage, string pollId)
    {
        await organizerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/" + pollId);
        await organizerPage.Locator("#poll-guest-link-count").FillAsync("1");
        await organizerPage.GetByRole(AriaRole.Button, new() { Name = "Create link" }).ClickAsync();
        return await organizerPage.Locator("span.font-mono").First.InnerTextAsync();
    }

    /// <summary>
    /// design §5 (verbatim): the row's three vote controls are an independent radiogroup, roving
    /// tabindex — only the checked control (or, unset, the first) is a tab stop, and ArrowRight/
    /// ArrowLeft cycle the choice with focus following the newly-checked control (ThemeToggle's
    /// shipped APG idiom).
    /// </summary>
    [Fact]
    public async Task VotingGrid_ArrowKeysCycleTheChoiceWithRovingTabindex_APG()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string pollId = await CreatePrivatePollAsync(
            organizer.Page, "Keyboard proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(2));
        await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/" + pollId);

        ILocator firstOptionCard = organizer.Page.Locator("[data-testid^='poll-option-']").First;
        ILocator available = firstOptionCard.Locator("[data-testid^='vote-available-']");
        ILocator maybe = firstOptionCard.Locator("[data-testid^='vote-maybe-']");
        ILocator unavailable = firstOptionCard.Locator("[data-testid^='vote-unavailable-']");

        // Nothing voted yet: only "Available" (the first choice) is a tab stop.
        await Assertions.Expect(available).ToHaveAttributeAsync("tabindex", "0");
        await Assertions.Expect(maybe).ToHaveAttributeAsync("tabindex", "-1");

        await available.FocusAsync();
        await organizer.Page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(maybe).ToHaveAttributeAsync("aria-checked", "true");
        await Assertions.Expect(maybe).ToBeFocusedAsync();
        await Assertions.Expect(available).ToHaveAttributeAsync("tabindex", "-1");
        await Assertions.Expect(maybe).ToHaveAttributeAsync("tabindex", "0");

        await organizer.Page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(unavailable).ToHaveAttributeAsync("aria-checked", "true");
        await Assertions.Expect(unavailable).ToBeFocusedAsync();

        // Wraps past the last choice back to the first.
        await organizer.Page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(available).ToHaveAttributeAsync("aria-checked", "true");
        await Assertions.Expect(available).ToBeFocusedAsync();
    }

    /// <summary>The S6 gate (design §6 slice table): revising an already-cast vote keeps focus on
    /// the clicked control across the refetch — the grid never steals focus on a successful mutation.</summary>
    [Fact]
    public async Task RevisingAnExistingVote_KeepsFocusOnTheClickedControl_S6Gate()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string pollId = await CreatePrivatePollAsync(
            organizer.Page, "Revise proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(2));
        await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/" + pollId);

        ILocator firstOptionCard = organizer.Page.Locator("[data-testid^='poll-option-']").First;
        ILocator available = firstOptionCard.Locator("[data-testid^='vote-available-']");
        ILocator maybe = firstOptionCard.Locator("[data-testid^='vote-maybe-']");

        await available.ClickAsync();
        await Assertions.Expect(available).ToHaveAttributeAsync("aria-checked", "true");
        await organizer.Page.GetByText("Your vote was saved.").WaitForAsync();

        // Revise: click a different control on the same row after the first vote round-tripped.
        await maybe.ClickAsync();
        await Assertions.Expect(maybe).ToHaveAttributeAsync("aria-checked", "true");
        await organizer.Page.GetByText("Your vote was saved.").WaitForAsync();
        await Assertions.Expect(maybe).ToBeFocusedAsync();
    }

    /// <summary>
    /// Phase 5 S7 lesson (TRACEABILITY.md, PHASE6_DESIGN.md §6 S6 row): a real-DOM layout-shape
    /// assertion, not just presence — the three vote controls in one option's row share a top
    /// (Y) coordinate, proving the row lays out horizontally rather than stacking.
    /// </summary>
    [Fact]
    public async Task VotingGrid_TheThreeControlsInOneRowShareATopCoordinate_LayoutShape()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string pollId = await CreatePrivatePollAsync(
            organizer.Page, "Layout proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(2));
        await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/" + pollId);

        ILocator firstOptionCard = organizer.Page.Locator("[data-testid^='poll-option-']").First;
        ILocator available = firstOptionCard.Locator("[data-testid^='vote-available-']");
        ILocator maybe = firstOptionCard.Locator("[data-testid^='vote-maybe-']");
        ILocator unavailable = firstOptionCard.Locator("[data-testid^='vote-unavailable-']");

        LocatorBoundingBoxResult? a = await available.BoundingBoxAsync();
        LocatorBoundingBoxResult? b = await maybe.BoundingBoxAsync();
        LocatorBoundingBoxResult? c = await unavailable.BoundingBoxAsync();

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);
        Assert.True(Math.Abs(a!.Y - b!.Y) < 1, $"same-row controls must share a top edge (got {a.Y} vs {b.Y})");
        Assert.True(Math.Abs(b.Y - c!.Y) < 1, $"same-row controls must share a top edge (got {b.Y} vs {c.Y})");
        // And they are laid out left-to-right, not stacked (distinct X positions).
        Assert.True(a.X < b.X && b.X < c.X);
    }

    /// <summary>
    /// design §5.4: explicit claim with display-name capture (D-017) over the poll-typed guest
    /// transport, then a vote on the shared voting grid — proving the guest and registered arms
    /// share one component over two transports.
    /// </summary>
    [Fact]
    public async Task Guest_ClaimsThenVotes_OverThePollGuestTransport_D017()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string pollId = await CreatePrivatePollAsync(
            organizer.Page, "Guest vote proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(2));
        string url = await MintPollGuestLinkAsync(organizer.Page, pollId);

        IBrowserContext guestContext = await fixture.NewContextAsync();
        IPage guestPage = await guestContext.NewPageAsync();
        await guestPage.GotoAsync(url);

        await guestPage.GetByLabel("Your name").FillAsync("Guesty McVote");
        await guestPage.GetByRole(AriaRole.Button, new() { Name = "Join and vote" }).ClickAsync();

        ILocator firstOptionCard = guestPage.Locator("[data-testid^='poll-option-']").First;
        ILocator available = firstOptionCard.Locator("[data-testid^='vote-available-']");
        await Assertions.Expect(available).Not.ToBeDisabledAsync();
        await available.ClickAsync();

        await Assertions.Expect(available).ToHaveAttributeAsync("aria-checked", "true");
        await guestPage.GetByText("Your vote was saved.").WaitForAsync();
        Assert.DoesNotContain("token", guestPage.Url, StringComparison.OrdinalIgnoreCase);
    }
}
