using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace PartyUp.BrowserTests.Accessibility;

/// <summary>
/// A-082/P5-AXE-02 (PHASE5_DESIGN.md §9, §11 "Browser lane (S1)"): axe-core over every checked-in
/// Phase 4 route (<see cref="AuditedRoutes"/>) at the shipped WCAG 2.2 AA tag set — any violation of
/// any impact fails unless it is a dated, route+rule-scoped entry in <see cref="AxeExclusions"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PlaywrightSuite.Name)]
public sealed class AxeAuditTests(PlaywrightFixture fixture)
{
    private static readonly string[] WcagTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
    private static readonly JsonSerializerOptions DumpJsonOptions = new() { WriteIndented = true };

    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static AuditContext? _context;

    private sealed record AuditContext(
        RegisteredUser Organizer,
        string SessionId,
        string GuestLinkUrl,
        string OwnFriendCode);

    /// <summary>
    /// One real registered account, one private session and one guest link — enough live entity ids
    /// to resolve every dynamic-segment route in <see cref="AuditedRoutes"/>. Built once and shared
    /// across every axe fact the same way the flow-test classes share their setup.
    /// <para>
    /// Deliberately a single registration, and a <b>private</b> session (D-099 exempts private
    /// sessions from needing an invited friend): the flow-test classes sharing this fixture's one
    /// API process already spend three of the five allowed `register-ip` calls
    /// (`RateLimitPolicies.cs`, 5/user-IP/hour) on <c>SessionRegisteredFlowTests</c> and one more on
    /// <c>SessionGuestFlowTests</c>. A second axe-only account would push the shared process to six
    /// registrations inside the same rolling hour and 429 — this class stays at exactly one so the
    /// whole collection's total (five) never crosses the ceiling. `{username}`/`{code}` routes below
    /// resolve against the organizer's own identity rather than a second party's.
    /// </para>
    /// </summary>
    private async Task<AuditContext> GetContextAsync()
    {
        if (_context is not null)
        {
            return _context;
        }

        await SetupLock.WaitAsync();
        try
        {
            if (_context is not null)
            {
                return _context;
            }

            RegisteredUser organizer = await fixture.RegisterAndOnboardAsync(
                await fixture.NewContextAsync(), "axeorganizer", "Axe Organizer");

            await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/new");
            await organizer.Page.Locator("#field-title").FillAsync("Axe audit session");
            await organizer.Page.Locator("#field-start").FillAsync(
                DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));
            await organizer.Page.Locator("#field-duration").FillAsync("60");
            await organizer.Page.Locator("#field-min").FillAsync("1");
            await organizer.Page.Locator("#field-max").FillAsync("4");
            await organizer.Page.GetByLabel("Private session").CheckAsync();
            await organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Create session" }).ClickAsync();
            await organizer.Page.WaitForURLAsync(url => Regex.IsMatch(url, @"/sessions/[0-9a-fA-F-]{36}$"));
            string sessionId = new Uri(organizer.Page.Url).Segments[^1];

            await organizer.Page.GotoAsync(PlaywrightFixture.WebBaseUrl + "/sessions/" + sessionId + "/invite");
            await organizer.Page.Locator("#guest-link-count").FillAsync("1");
            await organizer.Page.GetByRole(AriaRole.Button, new() { Name = "Create guest link" }).ClickAsync();
            string guestLinkUrl = await organizer.Page.Locator("span.font-mono").First.InnerTextAsync();

            // GetOwnFriendCodeAsync reads the ShareCard, only rendered while the account has zero
            // friends (PlaywrightFixture doc comment) — true here since this account never befriends
            // anyone. Read after the invite step so this navigation doesn't disturb it.
            string ownFriendCode = await PlaywrightFixture.GetOwnFriendCodeAsync(organizer.Page);

            _context = new AuditContext(organizer, sessionId, guestLinkUrl, ownFriendCode);
            return _context;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    public static IEnumerable<object[]> Routes() => AuditedRoutes.All.Select(route => new object[] { route });

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryAuditedRouteHasNoWcagViolationOutsideTheDatedExclusionList_D139(AuditedRoute route)
    {
        AuditContext context = await GetContextAsync();

        IBrowserContext? guestContext = null;
        IPage page;
        if (route.RequiresGuestLink)
        {
            // Own short-lived context per guest-route audit (never the organizer's persistent one) —
            // closed below regardless of outcome so a 21-route loop doesn't accumulate open browser
            // contexts across the run.
            guestContext = await fixture.NewContextAsync();
            page = await guestContext.NewPageAsync();
            await page.GotoAsync(context.GuestLinkUrl);
        }
        else
        {
            string path = route.PathTemplate
                .Replace("{sessionId}", context.SessionId, StringComparison.Ordinal)
                .Replace("{username}", context.Organizer.Username, StringComparison.Ordinal)
                .Replace("{code}", context.OwnFriendCode, StringComparison.Ordinal);
            page = context.Organizer.Page;
            await page.GotoAsync(PlaywrightFixture.WebBaseUrl + path);
        }

        AxeResultItem[] unexpected;
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            AxeResult result = await page.RunAxe(new AxeRunOptions
            {
                RunOnly = RunOnlyOptions.Tags(WcagTags),
            });

            HashSet<string> excludedRuleIds = AxeExclusions.Entries
                .Where(entry => entry.RouteName == route.Name)
                .Select(entry => entry.RuleId)
                .ToHashSet(StringComparer.Ordinal);

            unexpected = result.Violations
                .Where(violation => !excludedRuleIds.Contains(violation.Id))
                .ToArray();
        }
        finally
        {
            if (guestContext is not null)
            {
                await guestContext.CloseAsync();
            }
        }

        if (unexpected.Length > 0)
        {
            Directory.CreateDirectory(AxeArtifactsDirectory);
            string dumpPath = Path.Combine(AxeArtifactsDirectory, $"{route.Name}.json");
            await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(unexpected, DumpJsonOptions));
        }

        Assert.True(
            unexpected.Length == 0,
            $"Route '{route.Name}' has {unexpected.Length} un-excluded axe violation(s): " +
            string.Join(", ", unexpected.Select(v => $"{v.Id} ({v.Impact})")) +
            $". Details in {AxeArtifactsDirectory}.");
    }

    private static string AxeArtifactsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "axe-results");
}

/// <summary>
/// The list-shape half of A-082/P5-AXE-02: not a suite over every entry, one gate over the list
/// itself. Every entry must name a real, non-Phase-5 audited route and a dated, traceable finding —
/// enforced structurally so a future slice cannot silently widen the list instead of shrinking it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AxeExclusionListTests
{
    /// <summary>
    /// Phase 5's own new routes (added by S6/S7, appended to <see cref="AuditedRoutes"/> after S1)
    /// are named here so the never-on-a-Phase-5-route half of the gate has something concrete to
    /// check even before those slices exist — this literal must grow if a future slice's route name
    /// collides with a Phase 4 one, but no shipped Phase 5 route does today.
    /// </summary>
    private static readonly HashSet<string> Phase5RouteNames = new(StringComparer.Ordinal)
    {
        "calendar", "calendar-agenda", "find-a-time", "discover",
    };

    [Fact]
    public void EntriesAreDatedRuleScopedTraceabilityCitedAndNeverOnAPhase5Route_D139()
    {
        HashSet<string> auditedRouteNames = AuditedRoutes.All
            .Select(route => route.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (AxeExclusion entry in AxeExclusions.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.RuleId));
            Assert.True(
                DateOnly.TryParse(entry.DiscoveredOn, out _),
                $"'{entry.RouteName}'/'{entry.RuleId}' carries an unparsable discovery date '{entry.DiscoveredOn}'.");
            Assert.False(
                string.IsNullOrWhiteSpace(entry.TraceabilityRow),
                $"'{entry.RouteName}'/'{entry.RuleId}' carries no TRACEABILITY.md citation.");
            Assert.True(
                auditedRouteNames.Contains(entry.RouteName),
                $"'{entry.RouteName}' is not a route in AuditedRoutes.cs.");
            Assert.False(
                Phase5RouteNames.Contains(entry.RouteName),
                $"'{entry.RouteName}' is a Phase 5 route; P5-AXE-02/A-082 forbid it from ever entering this list.");
        }
    }
}
