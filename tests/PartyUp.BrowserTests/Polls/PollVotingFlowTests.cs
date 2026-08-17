using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Polls;

/// <summary>
/// PHASE6_DESIGN.md §5/§6 S6 row: real-browser proof of the voting grid's APG radiogroup idiom
/// (roving tabindex, arrow-key traversal, focus retention on revise — the S6 gate), the layout
/// shape of a vote row, and the guest claim-then-vote flow over the poll-typed guest link.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightCalendarSuite.Name)]
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
            _organizer ??= await fixture.SeedRegisteredUserAsync(
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
    /// invited friend) with two candidate times, picked from the click-to-toggle
    /// `CandidateGrid` (`data-testid="candidate-slot-{dayIndex}-{minuteOfDay}"`, added on the
    /// port from the old `#field-option-value` + "Add time" date-input flow). Advancing to next
    /// week first — rather than reasoning about `firstOptionUtc` against the grid's current-week
    /// display — keeps every clicked slot unambiguously in the future regardless of today's
    /// weekday; `firstOptionUtc` is kept as a parameter only so callers (who use it purely to vary
    /// the poll title's uniqueness) don't need to change. Mirrors
    /// `SessionGuestFlowTests.CreatePrivateSessionAsync`'s barrier choice: the create POST's own
    /// response is the barrier (asserted 201), never the client's post-`invalidateQueries` redirect
    /// — a 4xx there would otherwise surface as a silent 30s navigation timeout instead of a loud
    /// assert. The created poll id comes straight off `CreatePollResponse.poll.id`.
    /// </summary>
    private static async Task<string> CreatePrivatePollAsync(IPage organizerPage, string title, DateTime firstOptionUtc)
    {
        await organizerPage.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/new");
        await organizerPage.Locator("#field-title").FillAsync(title);

        await organizerPage.GetByRole(AriaRole.Button, new() { Name = "Next week" }).ClickAsync();
        await organizerPage.GetByTestId("candidate-slot-1-1020").ClickAsync();
        await organizerPage.GetByTestId("candidate-slot-2-1020").ClickAsync();

        await organizerPage.Locator("#field-min").FillAsync("1");
        await organizerPage.Locator("#field-max").FillAsync("4");
        await organizerPage.GetByRole(AriaRole.Radiogroup, new() { Name = "Visibility" })
            .GetByRole(AriaRole.Radio, new() { Name = "Private" }).ClickAsync();

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

    /// <summary>
    /// PHASE8_DESIGN.md §3 S7: the poll grid's first real-browser keyboard/SR sweep. The Phase 6
    /// facts above prove the APG traversal idiom; what had never been proven in a browser is that a
    /// vote can be *cast* with the keyboard alone and that its result is observable to a screen
    /// reader — the tally is plain text, not a color or a chart, so this reads that text back off the
    /// live DOM rather than trusting the fill.
    /// </summary>
    [Fact]
    public async Task PollGrid_KeyboardOnlyVoteCast_UpdatesTheSrObservableTally_D139()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string pollId = await CreatePrivatePollAsync(
            organizer.Page, "SR tally proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(2));
        await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/" + pollId);

        ILocator firstOptionCard = organizer.Page.Locator("[data-testid^='poll-option-']").First;
        ILocator tally = firstOptionCard.Locator("[data-testid^='poll-option-tally-']");
        ILocator available = firstOptionCard.Locator("[data-testid^='vote-available-']");
        ILocator maybe = firstOptionCard.Locator("[data-testid^='vote-maybe-']");

        // The zero state is itself SR-observable text, not an absence of a glyph — the new page's
        // tally reads "{{responded}}/{{total}} replied" rather than the old "No votes yet."/
        // "N available" copy. The private poll's sole roster row is the organizer, so total is 1.
        await Assertions.Expect(tally).ToHaveTextAsync("0/1 replied");

        // Keyboard alone: the row's single tab stop, then Space — the ARIA radio activation key.
        // No click, no pointer anywhere in this fact.
        await available.FocusAsync();
        await Assertions.Expect(available).ToBeFocusedAsync();
        await organizer.Page.Keyboard.PressAsync(" ");

        await organizer.Page.GetByText("Your vote was saved.").WaitForAsync();
        await Assertions.Expect(available).ToHaveAttributeAsync("aria-checked", "true");
        // The tally a screen reader reads is the proof the vote landed — a count in words, which
        // no amount of fill color would supply.
        await Assertions.Expect(tally).ToHaveTextAsync("1/1 replied");

        // Roving tabindex followed the checked control (APG): exactly one tab stop per row.
        await Assertions.Expect(available).ToHaveAttributeAsync("tabindex", "0");
        await Assertions.Expect(maybe).ToHaveAttributeAsync("tabindex", "-1");
        Assert.Equal(
            1,
            await firstOptionCard.Locator("[role=radio][tabindex='0']").CountAsync());

        // And the control's own accessible name carries the value, never the glyph alone.
        await Assertions.Expect(available).ToHaveAttributeAsync(
            "aria-label", new Regex(", Available$"));
    }

    /// <summary>
    /// PHASE8_DESIGN.md §3 S7: suggest-option reached and submitted without a pointer, with its
    /// outcome announced through a live region rather than only appearing as a new row.
    /// </summary>
    [Fact]
    public async Task PollGrid_KeyboardOnlySuggestOption_AnnouncesAndAddsTheCandidate_D139()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string pollId = await CreatePrivatePollAsync(
            organizer.Page, "Suggest proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(2));
        await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/" + pollId);

        // One radiogroup per option — `[data-testid^='poll-option-']` would also match each row's
        // own `poll-option-tally-…` paragraph and count every option twice.
        ILocator optionRows = organizer.Page.Locator("[data-testid^='vote-radiogroup-']");
        // The poll is created with two candidate times; counting before they have rendered would
        // baseline at zero and make the assertion below meaningless.
        await Assertions.Expect(optionRows).ToHaveCountAsync(2);
        int before = await optionRows.CountAsync();

        // The suggest field now lives inside a dialog, opened by its own trigger button — the
        // old create-page date input this test replaced was always on-page.
        await organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Suggest a time" }).ClickAsync();

        ILocator field = organizer.Page.Locator("#suggest-option-value");
        await field.FocusAsync();
        await Assertions.Expect(field).ToBeFocusedAsync();
        // Distinct from BOTH candidate times the fixture created (`firstOptionUtc` and
        // `firstOptionUtc.AddDays(1)`, i.e. +2d and +3d), and floored to the hour so it also sits on
        // the 15-minute grid the field's own `step` expects. A colliding suggestion is accepted and
        // announced but adds no row, which is what made this assertion fail before.
        DateTime suggested = DateTime.UtcNow.AddDays(5);
        await field.FillAsync(OptionTimeValue(
            new DateTime(suggested.Year, suggested.Month, suggested.Day, suggested.Hour, 0, 0, DateTimeKind.Utc)));

        // Activation is the WCAG 2.1.1 claim: the submit control is reached and fired by keyboard.
        ILocator submit = organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Suggest" });
        await submit.FocusAsync();
        await Assertions.Expect(submit).ToBeFocusedAsync();
        await organizer.Page.Keyboard.PressAsync("Enter");

        // Announced, not merely rendered — the notice is a `role=status` live region.
        await Assertions
            .Expect(organizer.Page.GetByRole(AriaRole.Status)
                .Filter(new() { HasText = "Your suggestion was added." }))
            .ToBeVisibleAsync();
        await Assertions.Expect(optionRows).ToHaveCountAsync(before + 1);
    }

    /// <summary>
    /// PHASE8_DESIGN.md §3 S7: the guest arm of the same sweep — a guest reaches the claim step and
    /// casts a vote over the poll-typed guest transport without a pointer, and sees the same
    /// SR-observable tally the registered arm does (one component, two transports).
    /// </summary>
    [Fact]
    public async Task PollGrid_GuestClaimsAndVotesWithTheKeyboardAlone_D139()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string pollId = await CreatePrivatePollAsync(
            organizer.Page, "Guest keyboard proof " + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(2));
        string url = await MintPollGuestLinkAsync(organizer.Page, pollId);

        IBrowserContext guestContext = await fixture.NewContextAsync();
        IPage guestPage = await guestContext.NewPageAsync();
        try
        {
            await guestPage.GotoAsync(url);

            ILocator nameField = guestPage.GetByLabel("Your name");
            await nameField.FocusAsync();
            await Assertions.Expect(nameField).ToBeFocusedAsync();
            await nameField.FillAsync("Keyboard Guest");

            ILocator join = guestPage.GetByRole(AriaRole.Button, new() { Name = "Join and vote" });
            await join.FocusAsync();
            await guestPage.Keyboard.PressAsync("Enter");

            ILocator firstOptionCard = guestPage.Locator("[data-testid^='poll-option-']").First;
            ILocator tally = firstOptionCard.Locator("[data-testid^='poll-option-tally-']");
            ILocator available = firstOptionCard.Locator("[data-testid^='vote-available-']");
            await Assertions.Expect(available).Not.ToBeDisabledAsync();
            // The guest transport still renders the old, unported `VotingGrid.tsx` (out of the
            // lovable port's scope) — its tally copy ("No votes yet." / "N available") is
            // unchanged, unlike the registered arm's `PollDetailPage.tsx` tally text above.
            await Assertions.Expect(tally).ToHaveTextAsync("No votes yet.");

            await available.FocusAsync();
            await Assertions.Expect(available).ToBeFocusedAsync();
            await guestPage.Keyboard.PressAsync(" ");

            await guestPage.GetByText("Your vote was saved.").WaitForAsync();
            await Assertions.Expect(available).ToHaveAttributeAsync("aria-checked", "true");
            await Assertions.Expect(tally).ToHaveTextAsync(new Regex(@"\b1 available\b"));
            Assert.Equal(
                1,
                await firstOptionCard.Locator("[role=radio][tabindex='0']").CountAsync());
            // A-064: the guest credential never reaches the address bar.
            Assert.DoesNotContain("token", guestPage.Url, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await guestContext.CloseAsync();
        }
    }

    /// <summary>
    /// PHASE6_DESIGN.md §6 S7 row's named gate: create→vote→finalize→session, end to end. The
    /// poll's deadline is a fixed 6/12/24/48h choice (`PollDeadlineChoice`) — no organizer action
    /// closes voting early — so a real browser run cannot wait out even the shortest choice.
    /// <see cref="PlaywrightFixture.BackdatePollPastDeadlineAsync"/> backdates the just-created
    /// poll's own `deadline` (and `selection_window_end`, kept D-088-consistent) a minute into the
    /// past on the same Postgres the running API reads from; the server's own real clock is what
    /// evaluates Finalize's guard from that point on — nothing about
    /// the finalize call itself is mocked or short-circuited.
    /// </summary>
    [Fact]
    public async Task Organizer_CreatesVotesFinalizesAndReachesTheSession_EndToEnd()
    {
        RegisteredUser organizer = await GetOrganizerAsync();
        string title = "Finalize proof " + Guid.NewGuid().ToString("N")[..6];
        string pollId = await CreatePrivatePollAsync(organizer.Page, title, DateTime.UtcNow.AddDays(2));

        // Vote first, while the poll is still open — the created poll's own two options.
        ILocator firstOptionCard = organizer.Page.Locator("[data-testid^='poll-option-']").First;
        await firstOptionCard.Locator("[data-testid^='vote-available-']").ClickAsync();
        await organizer.Page.GetByText("Your vote was saved.").WaitForAsync();

        await fixture.BackdatePollPastDeadlineAsync(pollId);

        await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/polls/" + pollId);
        // The status pill reads the raw materialized `poll.status` column (`statusLabelKey` has no
        // dedicated awaiting-selection label), which the backdate SQL alone does not flip — only a
        // request through the shared transition function does. The "Organizer has …" copy in the
        // summary card's countdown row is windowState-derived from the raw wire deadlines instead,
        // so it is the real signal that the awaiting-selection state was reached.
        // The same "Organizer has …" copy also renders in the sticky finalize bar; the summary
        // card's copy (matched first in DOM order) is proof enough that the window state landed.
        await organizer.Page.GetByText(new Regex("^Organizer has ")).First.WaitForAsync();

        // The new finalization UI has no option-selecting radio list: each option card carries its
        // own "Preview seats" finalize control (`finalize-option-{optionId}`), which opens the
        // preview/confirm dialog scoped to that option directly — the dialog's own body is where
        // "This preview and finalizing use the same rule" now lives, not the bare page.
        ILocator finalizeOption = organizer.Page.Locator("[data-testid^='finalize-option-']").First;
        await finalizeOption.ClickAsync();
        await organizer.Page.GetByText(
            "This preview and finalizing use the same rule").First.WaitForAsync();
        IResponse response = await organizer.Page.RunAndWaitForResponseAsync(
            () => organizer.Page.GetByTestId("finalize-confirm").ClickAsync(),
            candidate => candidate.Url.Contains("/finalize", StringComparison.Ordinal) &&
                candidate.Request.Method == "POST");
        Assert.Equal(200, response.Status);
        System.Text.Json.JsonElement body = (await response.JsonAsync())!.Value;
        string sessionId = body.GetProperty("sessionId").GetString()!;

        await organizer.Page.WaitForURLAsync(
            PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId);
        await organizer.Page.GetByTestId("screen-session-detail").WaitForAsync();
    }
}
