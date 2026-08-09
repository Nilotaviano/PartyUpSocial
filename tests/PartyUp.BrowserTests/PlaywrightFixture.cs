using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Playwright;
using Testcontainers.PostgreSql;

namespace PartyUp.BrowserTests;

/// <summary>
/// Orchestrates a real, black-box PartyUp stack for Playwright (PHASE4_DESIGN.md §12 S6 row,
/// PHASE5_DESIGN.md §9/A-082, ARCHITECTURE.md:73 "Browser E2E lane"). Unlike
/// <c>PartyUp.Api.IntegrationTests</c> (in-process <c>WebApplicationFactory</c> + Testcontainers), a
/// browser needs a real listening HTTP server it can navigate to. Two topologies, selected by the
/// <c>PARTYUP_BROWSER_TOPOLOGY</c> environment variable (documented at
/// <c>ARCHITECTURE.md:73</c>/`PHASE4_REFORECAST.md:66`'s canonical command):
/// <list type="bullet">
/// <item><b>local</b> (default — the Windows dev host has no Docker): spawns
/// <c>dotnet run --project src/PartyUp.Api</c> (Kestrel on the fixed dev port 5000, exactly the port
/// <c>web/vite.config.ts</c>'s proxy targets) and <c>npm run dev</c> (Vite on 5173, matching
/// <c>PublicBaseUrl</c> in <c>appsettings.json</c> so magic-link/verification emails point back at
/// it), both against the local dev Postgres already used for the WSL full-suite gate
/// (Host=localhost;Port=5432 — reachable from the Windows host per the documented local-dev setup).</item>
/// <item><b>built</b> (<c>PARTYUP_BROWSER_TOPOLOGY=built</c>, set by the CI <c>browser</c> job only):
/// the API serves its own <c>wwwroot</c> against a Testcontainers Postgres — no <c>dotnet run</c>, no
/// Vite dev server, matching the single-container prod topology (`ARCHITECTURE.md:12`). The job
/// prepares the built solution and a populated <c>wwwroot</c> (mirroring `deploy/Dockerfile:27`'s
/// placement) and starts neither process — this fixture starts the API from the built output and
/// fails the run outright if that <c>wwwroot</c> is empty, rather than auditing a 404 shell.</item>
/// </list>
/// <c>Database:MigrateOnStartup</c> is forced on in both topologies so the schema is current without
/// a manual EF step.
/// </summary>
public sealed class PlaywrightFixture : IAsyncLifetime, IDisposable
{
    private const string ApiBaseUrl = "http://localhost:5000";

    /// <summary>
    /// The whole browser lane (every class in <see cref="PlaywrightSuite"/>, one shared API
    /// process per `dotnet test` invocation) registers well more than production's 5/hour/IP
    /// `register-ip` ceiling — this fixture spawns the API with that dimension overridden via
    /// `RateLimits:RegisterPerIpPerHour` (`RateLimitPolicies.cs`, `Program.cs`) in both topologies,
    /// so the CI `browser` job's built topology inherits it too. Production reads no such
    /// configuration key, so its default (5) is untouched.
    /// </summary>
    private const string RegisterPerIpPerHourOverride = "100";

    /// <summary>
    /// A-082's selector. Anything other than the literal <c>"built"</c> (unset included) is the
    /// local topology — the property is opt-in the same way <c>PartyUpBrowserTests</c> is, so a
    /// plain developer run never accidentally reaches for Docker.
    /// </summary>
    private static bool IsBuiltTopology => string.Equals(
        Environment.GetEnvironmentVariable("PARTYUP_BROWSER_TOPOLOGY"),
        "built",
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// In the built topology the API serves the SPA itself (prod's single-container shape), so
    /// there is no separate Vite origin — this stays a static property (not an instance member) so
    /// every existing flow-test call site (<c>PlaywrightFixture.WebBaseUrl</c>) keeps working
    /// unchanged across both topologies.
    /// </summary>
    public static string WebBaseUrl { get; } = IsBuiltTopology ? ApiBaseUrl : "http://localhost:5173";

    /// <summary>
    /// F3 (round-1 fix): everything a CI red needs to diagnose — trx logs (`--results-directory`,
    /// `ci.yml`), the API child process's own stdout/stderr, axe findings
    /// (<c>Accessibility/AxeAuditTests.cs</c>'s own directory), and Playwright traces — lands under
    /// this one directory so `ci.yml`'s upload step has a single real path to point at.
    /// </summary>
    private static string ArtifactsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "test-results");

    private static int _registrationCount;

    /// <summary>
    /// Round-1 fix, gate-driven (this ticket's own two-consecutive-clean-run requirement exposed
    /// it; no F-numbered finding named it): <c>RateLimitPolicies.cs</c>'s "default" dimension
    /// (300/IP/minute, a FIXED window covering every operation, `AutoReplenishment = true`) is
    /// shared across the whole collection — every class's traffic comes from the same loopback IP
    /// and counts against the same budget. Each registration (age gate → register → verify →
    /// onboarding) is by far the densest traffic a single test action produces here, and this
    /// suite runs five of them (`AxeAuditTests` one, `SessionGuestFlowTests` one,
    /// `SessionRegisteredFlowTests` three) close enough together — timed by nothing but how fast
    /// Playwright can drive the UI, with no artificial pacing anywhere in the unmodified flow-test
    /// files — that two registrations' combined traffic can exceed 300 within whatever 60s window
    /// they land in. Confirmed by direct evidence: a captured 429 on `/api/v1/guest/{id}/session`,
    /// itself `retry:false` client-side, stranding an unmodified Phase 4 guest-flow test showing
    /// neither its expected text nor a visible error. A per-navigation delay only changes *when*
    /// one class's own requests happen, not how many a *different*, unmodified class fires once it
    /// starts, so the fix lives at the one call site every registration in the whole suite already
    /// shares: before the second and every later registration, wait out a full window with zero
    /// requests. `FixedWindowRateLimiter`'s replenishment does not depend on a request landing
    /// exactly on the boundary, so a quiet period at least as long as the window guarantees
    /// whichever window is open has fully reset before the next registration's burst begins — a
    /// barrier on the shared resource's state, not a wait tied to any one assertion.
    /// </summary>
    private static async Task SettleSharedRateLimitWindowBeforeRegistrationAsync()
    {
        if (Interlocked.Increment(ref _registrationCount) > 1)
        {
            await Task.Delay(65_000);
        }
    }

    private readonly string _repoRoot = FindRepoRoot();
    private readonly List<IBrowserContext> _tracedContexts = [];
    private readonly Lock _tracedContextsLock = new();
    private string _mailDropPath = string.Empty;
    private KillOnCloseJob? _job;
    private Process? _apiProcess;
    private Process? _webProcess;
    private PostgreSqlContainer? _postgresContainer;
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _mailDropPath = Path.Combine(_repoRoot, "tests", "PartyUp.BrowserTests", ".mail-drop");
        Directory.CreateDirectory(_mailDropPath);
        foreach (string stale in Directory.EnumerateFiles(_mailDropPath, "*.eml"))
        {
            File.Delete(stale);
        }

        if (OperatingSystem.IsWindows())
        {
            _job = new KillOnCloseJob();
        }

        if (IsBuiltTopology)
        {
            await InitializeBuiltTopologyAsync();
        }
        else
        {
            InitializeLocalTopology();
        }

        await WaitForHttpReadyAsync(ApiBaseUrl + "/health/live", TimeSpan.FromMinutes(3));
        if (!IsBuiltTopology)
        {
            await WaitForHttpReadyAsync(WebBaseUrl + "/", TimeSpan.FromMinutes(2));
        }

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    private void InitializeLocalTopology()
    {
        _apiProcess = StartProcess(
            "dotnet",
            "run --no-launch-profile --project src/PartyUp.Api --urls " + ApiBaseUrl,
            _repoRoot,
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["Database__MigrateOnStartup"] = "true",
                ["Email__DropPath"] = _mailDropPath,
                ["RateLimits__RegisterPerIpPerHour"] = RegisterPerIpPerHourOverride,
            },
            logName: "api");
        AssignToJob(_apiProcess);

        _webProcess = StartProcess(
            "cmd.exe",
            "/c npm run dev -- --port 5173 --strictPort",
            Path.Combine(_repoRoot, "web"),
            new Dictionary<string, string?>());
        AssignToJob(_webProcess);
    }

    /// <summary>
    /// A-082: starts a Testcontainers Postgres, then runs the API's own built output directly
    /// (never <c>dotnet run</c>) against it. The <c>browser</c> CI job builds the solution and then
    /// copies <c>web/dist</c> straight into <c>src/PartyUp.Api/bin/Debug/net10.0/wwwroot</c> —
    /// <c>dotnet build</c> (unlike <c>dotnet publish</c>) never copies a project's own
    /// <c>wwwroot</c> into its output directory, so the job must place the built assets directly
    /// in the bin output itself, after the build, not the source tree before it. That physical
    /// directory is what <see cref="BuiltApiOutputDirectory"/> is checked against below, and it is
    /// what the API actually serves: this method starts the API process with that directory as its
    /// working directory, ASP.NET Core's default content root is the process's current directory,
    /// and <c>Program.cs:357</c>'s <c>Path.Combine(ContentRootPath, "wwwroot")</c> — the check that
    /// gates <c>UseStaticFiles</c>/<c>MapFallbackToFile</c> — resolves against that content root.
    /// This path runs only where Docker exists (CI; the Windows dev host has none).
    /// </summary>
    private async Task InitializeBuiltTopologyAsync()
    {
        _postgresContainer = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _postgresContainer.StartAsync();

        string outputDirectory = BuiltApiOutputDirectory;
        string wwwrootPath = Path.Combine(outputDirectory, "wwwroot");
        if (!Directory.Exists(wwwrootPath) || !Directory.EnumerateFileSystemEntries(wwwrootPath).Any())
        {
            throw new InvalidOperationException(
                $"Built topology requires a populated '{wwwrootPath}'. The 'browser' CI job must run " +
                "'dotnet build PartyUp.sln' and THEN copy web/dist straight into that build's own " +
                "bin output (src/PartyUp.Api/bin/Debug/net10.0/wwwroot) — 'dotnet build' never " +
                "copies a project's wwwroot the way 'dotnet publish' does, so copying into the " +
                "source tree or copying before the build both leave this directory empty. A-082's " +
                "fixture never audits a 404 shell.");
        }

        string dllPath = Path.Combine(outputDirectory, "PartyUp.Api.dll");
        if (!File.Exists(dllPath))
        {
            throw new InvalidOperationException(
                $"Built topology requires '{dllPath}'. Run 'dotnet build PartyUp.sln' before this fixture.");
        }

        _apiProcess = StartProcess(
            "dotnet",
            $"exec \"{dllPath}\" --urls {ApiBaseUrl}",
            outputDirectory,
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["Database__MigrateOnStartup"] = "true",
                ["Email__DropPath"] = _mailDropPath,
                ["ConnectionStrings__Db"] = _postgresContainer.GetConnectionString(),
                ["PublicBaseUrl"] = ApiBaseUrl,
                ["RateLimits__RegisterPerIpPerHour"] = RegisterPerIpPerHourOverride,
            },
            logName: "api");
        AssignToJob(_apiProcess);
    }

    /// <summary>The directory the CI <c>browser</c> job's own <c>dotnet build</c> writes to.</summary>
    private string BuiltApiOutputDirectory =>
        Path.Combine(_repoRoot, "src", "PartyUp.Api", "bin", "Debug", "net10.0");

    /// <summary>
    /// A same-DB-instance, real-server-clock backdoor for tests that need to prove a real
    /// time-gated transition (e.g. a poll's minimum 6-hour deadline) without waiting hours for one
    /// in a browser lane. Runs <paramref name="sql"/> via <c>psql</c> against whichever Postgres
    /// this fixture's own API process is already pointed at — never a second connection this
    /// fixture doesn't otherwise own — so the API's very next read sees the write. No new package
    /// reference: the built topology already carries <c>Testcontainers.PostgreSql</c>'s own
    /// <c>ExecScriptAsync</c> (a <c>docker exec psql</c> wrapper); the local topology shells out to
    /// the same <c>docker compose</c> Postgres this repo's local dev setup already requires
    /// (CLAUDE.md's <c>deploy/compose.dev.yml</c>), matching its documented default credentials.
    /// </summary>
    public async Task RunSqlBackdoorAsync(string sql)
    {
        if (IsBuiltTopology)
        {
            Testcontainers.PostgreSql.PostgreSqlContainer container = _postgresContainer
                ?? throw new InvalidOperationException("Built topology must have started its Postgres container.");
            var result = await container.ExecScriptAsync(sql);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"SQL backdoor failed (exit {result.ExitCode}): {result.Stderr}");
            }
            return;
        }

        var info = new ProcessStartInfo(
            "docker",
            "compose -f deploy/compose.dev.yml exec -T postgres psql -U partyup -d partyup -v ON_ERROR_STOP=1")
        {
            WorkingDirectory = _repoRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start the local-topology SQL backdoor process.");
        await process.StandardInput.WriteAsync(sql);
        process.StandardInput.Close();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"SQL backdoor failed (exit {process.ExitCode}): {stderr}{stdout}");
        }
    }

    public async Task DisposeAsync()
    {
        // F3: export every still-live traced context's trace BEFORE closing the browser (closing
        // kills the contexts, and a context AxeAuditTests' own `finally` already closed cannot
        // export — best-effort per context so one already-closed context doesn't drop the rest).
        if (IsBuiltTopology)
        {
            List<IBrowserContext> contexts;
            lock (_tracedContextsLock)
            {
                contexts = [.. _tracedContexts];
            }

            Directory.CreateDirectory(ArtifactsDirectory);
            int index = 0;
            foreach (IBrowserContext context in contexts)
            {
                try
                {
                    await context.Tracing.StopAsync(new()
                    {
                        Path = Path.Combine(ArtifactsDirectory, $"trace-{index++}.zip"),
                    });
                }
                catch
                {
                    // Best-effort: a context closed earlier by test code has nothing left to export.
                }
            }
        }

        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();
        KillProcessTree(_webProcess);
        KillProcessTree(_apiProcess);
        if (_postgresContainer is not null)
        {
            await _postgresContainer.DisposeAsync();
        }

        Dispose();
    }

    // Closing the job handle is what actually kills any surviving descendants
    // (KILL_ON_JOB_CLOSE); SafeHandle disposal is idempotent, so running after
    // DisposeAsync as well is harmless.
    public void Dispose() => _job?.Dispose();

    public async Task<IBrowserContext> NewContextAsync(string? timezoneId = null)
    {
        IBrowserContext context = await Browser.NewContextAsync(new() { TimezoneId = timezoneId });
        context.SetDefaultNavigationTimeout(NavigationTimeoutMs);
        context.SetDefaultTimeout(ActionTimeoutMs);

        if (IsBuiltTopology)
        {
            // F3: CI-only (the topology check keeps this cost off every local run) unconditional
            // tracing, so a red CI run has a real trace to open rather than only a log line.
            await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
            lock (_tracedContextsLock)
            {
                _tracedContexts.Add(context);
            }
        }

        return context;
    }

    /// <summary>
    /// Round-1 F7: F2's deterministic response-barrier fix (`SessionGuestFlowTests.cs`'s
    /// `CreatePrivateSessionAsync`) removed the client-navigation race that made a 60s ceiling look
    /// necessary, so this override states Playwright's own default explicitly at the one call site
    /// every context goes through, rather than widening it. Two consecutive cold 46-test runs (this
    /// ticket's gate, after this file's separate rate-limit-window fix below — a defect distinct
    /// from F2, gate-driven rather than F-numbered) were green at this un-raised value.
    /// </summary>
    private const int NavigationTimeoutMs = 30000;
    private const int ActionTimeoutMs = 30000;

    /// <summary>
    /// Drives a brand-new registered account through the real magic-link-style flow the
    /// web app exposes (age gate → email-only register → mailbox link → password → onboarding
    /// wizard) so the browser tests exercise the same path a real user takes rather than a
    /// seeded/short-circuited session. Returns a page already at <c>/app</c>, plus the username
    /// the caller needs for friend-graph and roster assertions.
    /// </summary>
    /// <param name="onOnboardingStep">
    /// Round-1 F5: invoked once per onboarding step (<c>username</c>, <c>display-name</c>,
    /// <c>avatar</c>, <c>time-zone</c>, <c>link-platforms</c>, <c>friends</c>), with the page settled
    /// on that step and before it advances — the only way to reach any onboarding step's rendered
    /// page at all, since <c>OnboardingWizard.tsx</c> redirects every one of its routes to the
    /// <c>friends</c> step once the account's onboarding is complete (`isActive` in that component),
    /// and redirects a request past whatever step the draft has actually reached back to that step
    /// otherwise. <see cref="Accessibility.AxeAuditTests"/> is this parameter's only caller; the
    /// ordinary flow-test callers (unmodified) pass <see langword="null"/> and walk straight through.
    /// </param>
    public async Task<RegisteredUser> RegisterAndOnboardAsync(
        IBrowserContext context,
        string emailLocalPart,
        string displayName,
        Func<string, IPage, Task>? onOnboardingStep = null)
    {
        await SettleSharedRateLimitWindowBeforeRegistrationAsync();

        // A random suffix on both the email and the username: the dev Postgres this fixture
        // points at is not reset between runs, so a fixed email would hit RegisterWithPasswordUseCase's
        // "existing account" branch (a different, link-less email template) on any rerun.
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string email = $"{emailLocalPart}-{suffix}@partyup-browsertests.example";
        string username = (emailLocalPart + suffix).ToLowerInvariant();
        DateTime since = DateTime.UtcNow;
        IPage page = await context.NewPageAsync();

        await page.GotoAsync(WebBaseUrl + "/register");
        await page.GetByLabel("Email address").FillAsync(email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();

        // First-ever submit routes through the age gate (`attested()` is false until it is
        // passed once); completing it returns to a freshly mounted `/register` with the email
        // field empty again, so the submission is repeated once attested. The client-side
        // `navigate()` that lands on `/age-gate` is not guaranteed to have landed the instant the
        // click resolves (SPA history update vs. Playwright's URL tracking), so this waits on the
        // page itself — the age-gate checkbox or the "sent" confirmation, whichever wins — rather
        // than reading `page.Url` synchronously right after the click.
        // The age-gate checkbox is a zero-size Radix primitive (no explicit box styling), so it
        // never satisfies Playwright's default "visible" wait state — waiting/checking for
        // "Attached" (DOM presence) is what actually distinguishes the two outcomes here.
        ILocator ageGateCheckbox = page.Locator("#age-gate");
        ILocator sentConfirmation = page.GetByText("Check your inbox.");
        await Task.WhenAny(
            ageGateCheckbox.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = 10000,
            }),
            sentConfirmation.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 }));

        if (await ageGateCheckbox.CountAsync() > 0)
        {
            // The Radix checkbox primitive is unstyled here (no explicit box size/position), so
            // Playwright cannot compute an on-screen click point even with Force; it is still a
            // real, keyboard/SR-accessible control, so a real DOM click event is dispatched
            // directly rather than simulated through hit-testing.
            await page.Locator("#age-gate").DispatchEventAsync("click");
            await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("/register", StringComparison.Ordinal));
            await page.GetByLabel("Email address").FillAsync(email);
            await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        }

        await page.GetByText("Check your inbox.").WaitForAsync();

        string verifyLink = await HarvestLinkAsync("/auth/verify-email#token=", since);
        await page.GotoAsync(verifyLink);
        await page.GetByLabel("Password").FillAsync("correct horse battery staple 42");
        await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

        await page.WaitForURLAsync(url => url.Contains("/onboarding", StringComparison.Ordinal));
        if (onOnboardingStep is not null)
        {
            await onOnboardingStep("username", page);
        }

        await page.GetByLabel("Username").FillAsync(username);
        await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

        await page.GetByLabel("Display name").WaitForAsync();
        if (onOnboardingStep is not null)
        {
            await onOnboardingStep("display-name", page);
        }

        await page.GetByLabel("Display name").FillAsync(displayName);
        await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

        // Avatar step: default selection is fine, just continue.
        await page.WaitForURLAsync(url => url.EndsWith("/onboarding/avatar", StringComparison.Ordinal));
        if (onOnboardingStep is not null)
        {
            await onOnboardingStep("avatar", page);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

        // Time-zone step: browser-detected default is fine.
        await page.WaitForURLAsync(url => url.EndsWith("/onboarding/time-zone", StringComparison.Ordinal));
        if (onOnboardingStep is not null)
        {
            await onOnboardingStep("time-zone", page);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

        // Link-platforms step: optional, completes onboarding.
        await page.WaitForURLAsync(url => url.EndsWith("/onboarding/link-platforms", StringComparison.Ordinal));
        if (onOnboardingStep is not null)
        {
            await onOnboardingStep("link-platforms", page);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

        await page.WaitForURLAsync(url => url.EndsWith("/onboarding/friends", StringComparison.Ordinal));
        if (onOnboardingStep is not null)
        {
            await onOnboardingStep("friends", page);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Finish setup" }).ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith("/app", StringComparison.Ordinal));
        return new RegisteredUser(page, username, displayName);
    }

    /// <summary>
    /// Reads the account's own friend code off `/friends`'s `ShareCard` (only rendered while the
    /// account has zero friends, which is always true for these freshly registered accounts).
    /// </summary>
    public static async Task<string> GetOwnFriendCodeAsync(IPage page)
    {
        await page.GotoAsync(WebBaseUrl + "/friends");
        return await page.Locator("p.font-mono").First.InnerTextAsync();
    }

    /// <summary>
    /// Establishes a real bilateral friendship (D-099 requires an invited *friend* for a
    /// non-private session): <paramref name="requester"/> resolves <paramref name="recipient"/>'s
    /// friend code and sends a request; <paramref name="recipient"/> accepts it from `/friends`.
    /// </summary>
    public static async Task BefriendAsync(RegisteredUser requester, RegisteredUser recipient)
    {
        string recipientCode = await GetOwnFriendCodeAsync(recipient.Page);
        await requester.Page.GotoAsync(WebBaseUrl + "/add/" + recipientCode);
        await requester.Page.GetByRole(AriaRole.Button, new() { Name = "Send friend request" }).ClickAsync();
        // Wait for the mutation's own success confirmation (not just the click event) before the
        // recipient reloads `/friends` — otherwise the recipient can load ahead of the write.
        await requester.Page.GetByText("Friend request sent.").WaitForAsync();

        await recipient.Page.GotoAsync(WebBaseUrl + "/friends");
        await recipient.Page
            .GetByRole(AriaRole.Button, new() { Name = $"Accept request @{requester.Username}" })
            .ClickAsync();
    }

    /// <summary>
    /// Switches the account's display language through the real `/settings/account` selector
    /// (design §10.2/D-146) rather than a test-only shortcut.
    /// </summary>
    public static async Task SwitchLanguageAsync(IPage page, string optionLabel)
    {
        await page.GotoAsync(WebBaseUrl + "/settings/account");
        // `#locale` (the trigger's id), not its localized `aria-label` — once the account's
        // locale actually changes, "Display language" itself renders in the new language, so a
        // label-text locator would only ever work for the very first switch away from English.
        await page.Locator("#locale").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = optionLabel }).ClickAsync();
        // The saved-confirmation renders in the *new* language (the switch is synchronous with
        // the confirmation in `AccountSettingsPage`'s `onSuccess`), so this waits on the
        // language-neutral `role="status"` region rather than an English string.
        await page.Locator("[role='status']").First.WaitForAsync();
    }

    /// <summary>
    /// §9.1/A-064's mailbox harvesting for local e2e: `LoggingEmailSender` (Development-only) drops
    /// each outbound email as a `.eml` file. This polls for the newest file created after
    /// <paramref name="since"/> and extracts the first link containing <paramref name="pathContains"/>.
    /// </summary>
    public async Task<string> HarvestLinkAsync(string pathContains, DateTime since, TimeSpan? timeout = null)
    {
        DateTime sinceWithSkewGuard = since - TimeSpan.FromSeconds(2);
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        while (DateTime.UtcNow < deadline)
        {
            IEnumerable<string> candidates = Directory.EnumerateFiles(_mailDropPath, "*.eml")
                .Where(path => File.GetLastWriteTimeUtc(path) >= sinceWithSkewGuard)
                .OrderByDescending(File.GetLastWriteTimeUtc);

            foreach (string file in candidates)
            {
                string content = await File.ReadAllTextAsync(file);
                Match match = Regex.Match(content, @"https?://\S*" + Regex.Escape(pathContains) + @"\S*");
                if (match.Success)
                {
                    return match.Value.TrimEnd('.', ')', ']', '\r', '\n');
                }
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"No dropped email contained a link with '{pathContains}' within the timeout.");
    }

    /// <summary>
    /// F3 (round-1 fix): when <paramref name="logName"/> is supplied, this process's stdout/stderr
    /// are appended to <c>{logName}.stdout.log</c>/<c>{logName}.stderr.log</c> under
    /// <see cref="ArtifactsDirectory"/> instead of being discarded — a CI red on the API side used
    /// to leave nothing to diagnose beyond the Playwright-side assertion failure.
    /// </summary>
    private static Process StartProcess(
        string fileName,
        string arguments,
        string workingDirectory,
        IDictionary<string, string?> environment,
        string? logName = null)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (KeyValuePair<string, string?> pair in environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        StreamWriter? stdout = null;
        StreamWriter? stderr = null;
        if (logName is not null)
        {
            Directory.CreateDirectory(ArtifactsDirectory);
            stdout = new StreamWriter(Path.Combine(ArtifactsDirectory, $"{logName}.stdout.log")) { AutoFlush = true };
            stderr = new StreamWriter(Path.Combine(ArtifactsDirectory, $"{logName}.stderr.log")) { AutoFlush = true };
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout?.WriteLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr?.WriteLine(e.Data);
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForHttpReadyAsync(string url, TimeSpan timeout)
    {
        using var http = new HttpClient();
        DateTime deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                HttpResponseMessage response = await http.GetAsync(url);
                if ((int)response.StatusCode < 500)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"'{url}' did not become ready within {timeout}.", last);
    }

    private void AssignToJob(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            _job!.Assign(process);
        }
    }

    private static void KillProcessTree(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best-effort teardown; a leaked dev-server process is a local-only nuisance.
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Walks up from this source file's own compile-time path rather than
    /// <see cref="AppContext.BaseDirectory"/>: the mandatory WSL full-suite gate redirects test
    /// output under <c>-p:ArtifactsPath=/tmp/...</c>, which is outside the repository tree
    /// entirely, so a base-directory walk-up can never find <c>PartyUp.sln</c> there. The compiler
    /// embeds the real source path (under the repo, on whichever OS actually compiled it) via
    /// <see cref="CallerFilePathAttribute"/>, which output redirection cannot change.
    /// </summary>
    private static string FindRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        DirectoryInfo? directory = new FileInfo(sourceFilePath).Directory;
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PartyUp.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (PartyUp.sln) above " + sourceFilePath);
    }
}

[CollectionDefinition(Name)]
public sealed class PlaywrightSuite : ICollectionFixture<PlaywrightFixture>
{
    public const string Name = "Playwright browser suite";
}

public sealed record RegisteredUser(IPage Page, string Username, string DisplayName);
