using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Playwright;
using Npgsql;
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

    /// <summary>
    /// PHASE7_DESIGN.md §6: <c>Push:DispatchIntervalSeconds</c>'s value in this lane —
    /// production never sets this key and keeps the minutely Hangfire cadence instead
    /// (<see cref="PartyUp.Api.Hosting.PushDispatchTriggerService"/>'s own remarks).
    /// </summary>
    private const string PushDispatchIntervalSecondsOverride = "2";

    private readonly string _repoRoot = FindRepoRoot();
    private readonly List<IBrowserContext> _tracedContexts = [];
    private readonly Lock _tracedContextsLock = new();
    private string _mailDropPath = string.Empty;
    private string _pushDropPath = string.Empty;
    private string _pushVapidPublicKey = string.Empty;
    private string _pushVapidPrivateKey = string.Empty;
    private KillOnCloseJob? _job;
    private Process? _apiProcess;
    private Process? _webProcess;
    private PostgreSqlContainer? _postgresContainer;
    private IPlaywright? _playwright;

    /// <summary>
    /// The exact `ConnectionStrings:Db` value the spawned API process reads, in whichever
    /// topology — set once, in the same place each topology computes what it hands the API, so
    /// <see cref="BackdatePollPastDeadlineAsync"/> can never target a different database than the one under
    /// test (F1 fix: no more topology-specific guessing at credentials or a `docker` CLI
    /// dependency the Windows dev host doesn't have, ARCHITECTURE.md:238).
    /// </summary>
    private string _dbConnectionString = string.Empty;

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _mailDropPath = Path.Combine(_repoRoot, "tests", "PartyUp.BrowserTests", ".mail-drop");
        Directory.CreateDirectory(_mailDropPath);
        foreach (string stale in Directory.EnumerateFiles(_mailDropPath, "*.eml"))
        {
            File.Delete(stale);
        }

        // PHASE7_DESIGN.md §6: the push-drop analogue of the mail-drop above — one JSON file per
        // `LoggingPushSender` send, cleared per run so a stale file from a prior run can never be
        // mistaken for this run's own delivery.
        _pushDropPath = Path.Combine(_repoRoot, "tests", "PartyUp.BrowserTests", ".push-drop");
        Directory.CreateDirectory(_pushDropPath);
        foreach (string stale in Directory.EnumerateFiles(_pushDropPath, "*.json"))
        {
            File.Delete(stale);
        }

        (_pushVapidPublicKey, _pushVapidPrivateKey) = GenerateVapidKeyPair();

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
        // Explicitly computed and handed to the API rather than left to its own
        // `appsettings.json` default — the exact string this fixture then also opens for
        // `BackdatePollPastDeadlineAsync`, never a hardcoded guess at the local dev credentials
        // (`deploy/compose.dev.yml`'s own comment cites the same default this falls back to,
        // an environment override always wins, matching how `Database__MigrateOnStartup`
        // above already flows through `ConnectionStrings__Db`'s double-underscore convention).
        _dbConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Db")
            ?? "Host=localhost;Port=5432;Database=partyup;Username=partyup;Password=partyup";

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
                ["ConnectionStrings__Db"] = _dbConnectionString,
                ["Push__DispatchIntervalSeconds"] = PushDispatchIntervalSecondsOverride,
                ["Push__Provider"] = "log",
                ["Push__DropPath"] = _pushDropPath,
                ["Push__Vapid__PublicKey"] = _pushVapidPublicKey,
                ["Push__Vapid__PrivateKey"] = _pushVapidPrivateKey,
                ["Push__Vapid__Subject"] = "mailto:push-lane@partyup-browsertests.example",
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
        _dbConnectionString = _postgresContainer.GetConnectionString();

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
                ["ConnectionStrings__Db"] = _dbConnectionString,
                ["PublicBaseUrl"] = ApiBaseUrl,
                ["RateLimits__RegisterPerIpPerHour"] = RegisterPerIpPerHourOverride,
                ["Push__DispatchIntervalSeconds"] = PushDispatchIntervalSecondsOverride,
                ["Push__Provider"] = "log",
                ["Push__DropPath"] = _pushDropPath,
                ["Push__Vapid__PublicKey"] = _pushVapidPublicKey,
                ["Push__Vapid__PrivateKey"] = _pushVapidPrivateKey,
                ["Push__Vapid__Subject"] = "mailto:push-lane@partyup-browsertests.example",
            },
            logName: "api");
        AssignToJob(_apiProcess);
    }

    /// <summary>The directory the CI <c>browser</c> job's own <c>dotnet build</c> writes to.</summary>
    private string BuiltApiOutputDirectory =>
        Path.Combine(_repoRoot, "src", "PartyUp.Api", "bin", "Debug", "net10.0");

    /// <summary>
    /// A same-DB-instance, real-server-clock backdoor for tests that need to prove a real
    /// time-gated poll transition (Finalize's own clock-inclusive guard) without waiting out the
    /// poll's real minimum 6-hour deadline in a browser lane. Opens a direct
    /// <see cref="NpgsqlConnection"/> against <see cref="_dbConnectionString"/> — the exact same
    /// string this fixture handed the running API process in whichever topology (F1: no `docker`
    /// CLI dependency, which the Windows dev host does not have, ARCHITECTURE.md:238; no
    /// topology-specific credential guessing) — so the API's very next read sees the write. Moves
    /// <c>deadline</c> and <c>selection_window_end</c> together, keeping D-088's
    /// <c>selection_window_end = deadline + 48h</c> invariant intact rather than backdating into a
    /// row shape the aggregate itself could never produce (F3). The poll id is a bound parameter,
    /// never interpolated into the command text (F1).
    /// </summary>
    public async Task BackdatePollPastDeadlineAsync(string pollId)
    {
        await using var connection = new NpgsqlConnection(_dbConnectionString);
        await connection.OpenAsync();

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(-1);
        DateTimeOffset selectionWindowEnd = deadline.AddHours(48);

        await using var command = new NpgsqlCommand(
            "UPDATE polls SET deadline = @deadline, selection_window_end = @selectionWindowEnd WHERE id = @pollId",
            connection);
        command.Parameters.AddWithValue("deadline", deadline);
        command.Parameters.AddWithValue("selectionWindowEnd", selectionWindowEnd);
        command.Parameters.AddWithValue("pollId", Guid.Parse(pollId));

        int rowsAffected = await command.ExecuteNonQueryAsync();
        if (rowsAffected != 1)
        {
            throw new InvalidOperationException(
                $"SQL backdoor expected to update exactly one poll row for '{pollId}', affected {rowsAffected}.");
        }
    }

    /// <summary>
    /// PHASE7_DESIGN.md §6's "asserts the subscription row" step: the same real-DB-connection
    /// backdoor idiom as <see cref="BackdatePollPastDeadlineAsync"/>, reading rather than writing —
    /// counts this user's active (never-revoked) `push_subscriptions` rows, the server-side proof
    /// the enable flow's `POST /api/v1/push/subscriptions` actually committed.
    /// </summary>
    public async Task<int> CountActivePushSubscriptionsForUsernameAsync(string username)
    {
        await using var connection = new NpgsqlConnection(_dbConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM push_subscriptions ps " +
            "JOIN users u ON u.id = ps.user_id " +
            "WHERE u.username = @username AND ps.revoked_at IS NULL",
            connection);
        command.Parameters.AddWithValue("username", username);

        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// PHASE8_DESIGN.md §3 S3's prescribed admin-fixture mechanism. <c>AdminSeeder</c> is an
    /// <c>IHostedService</c> keyed off <c>Admin:SeedUserIds</c>/<c>Admin:SeedEmails</c> and runs once at
    /// host startup — before any browser-lane account exists — so the lane cannot reach the role through
    /// configuration at all. It grants the role over this fixture's own connection instead (the same
    /// backdoor idiom as <see cref="BackdatePollPastDeadlineAsync"/>, against the exact connection string
    /// the running API was handed): the <c>auth_roles</c> row is guaranteed present because that same
    /// seeder calls <c>EnsureRoleExistsAsync</c> on every startup, and <c>auth_users.id</c> is the user's
    /// own id (<c>fk_auth_users_users_id</c>), so the join needs no second lookup.
    /// <para>
    /// Admin status resolves per request from the database (<c>CurrentUserProjectionReader</c> joins the
    /// role tables live), so the grant takes effect on the very next request no matter when the caller's
    /// cookie was issued — no re-login. The real constraint is that the account must already be onboarded
    /// and profile-complete, or <c>RequireAdmin</c> still answers 404; every account this fixture returns
    /// from <see cref="RegisterAndOnboardAsync"/> is. Callers still reload the page afterwards so cached
    /// TanStack queries refetch under the new role.
    /// </para>
    /// </summary>
    public async Task<Guid> GrantAdminRoleAsync(string username)
    {
        await using var connection = new NpgsqlConnection(_dbConnectionString);
        await connection.OpenAsync();

        await using var lookup = new NpgsqlCommand(
            "SELECT id FROM users WHERE username = @username",
            connection);
        lookup.Parameters.AddWithValue("username", username);
        if (await lookup.ExecuteScalarAsync() is not Guid userId)
        {
            throw new InvalidOperationException(
                $"No user row exists for '{username}'; the admin grant has nothing to target.");
        }

        await using var grant = new NpgsqlCommand(
            "INSERT INTO auth_user_roles (user_id, role_id) " +
            "SELECT @userId, r.id FROM auth_roles r WHERE r.normalized_name = 'ADMIN' " +
            "ON CONFLICT DO NOTHING",
            connection);
        grant.Parameters.AddWithValue("userId", userId);
        await grant.ExecuteNonQueryAsync();

        await using var verify = new NpgsqlCommand(
            "SELECT COUNT(*) FROM auth_user_roles ur " +
            "JOIN auth_roles r ON r.id = ur.role_id " +
            "WHERE ur.user_id = @userId AND r.normalized_name = 'ADMIN'",
            connection);
        verify.Parameters.AddWithValue("userId", userId);
        long granted = Convert.ToInt64(
            await verify.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        if (granted != 1)
        {
            throw new InvalidOperationException(
                $"The admin role grant for '{username}' left {granted} role row(s); expected exactly one. " +
                "AdminSeeder.EnsureRoleExistsAsync should have created the ADMIN role row at startup.");
        }

        return userId;
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
            // Returning attested, the register page resumes the stashed registration on its own
            // (prefill + auto-submit); racing it with a manual re-fill detaches the input
            // mid-fill. Wait for the resume's confirmation first — the prefilled form is visible
            // for a moment before the auto-submit replaces it, so an either-or wait would pick
            // the form and re-enter the race. Manual fill remains only as the stash-less
            // fallback (a full page load clears the in-memory stash).
            try
            {
                await page.GetByText("Check your inbox.").WaitForAsync(
                    new LocatorWaitForOptions { Timeout = 15000 });
            }
            catch (TimeoutException)
            {
                await page.GetByLabel("Email address").FillAsync(email);
                await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
            }
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

        // TOUR_DESIGN.md §1/§6: a fresh account lands on `/app` with a succeeded profile fetch and
        // `tourCompleted: false`, so the first-login tour now deterministically auto-opens here —
        // every one of this fixture's 14 call sites across 8 lane files would otherwise leak an open
        // tour into whatever that test does next. Dismissed with the shipped click-and-await idiom
        // (`PollVotingFlowTests.cs`'s `CreatePrivatePollAsync`: arming `RunAndWaitForResponseAsync`
        // AFTER the click would race a fast 204) so a failed completion POST fails this helper loudly
        // rather than silently leaving the overlay open for the caller's own first assertion.
        await page.GetByRole(AriaRole.Dialog).WaitForAsync();
        IResponse tourCompletedResponse = await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new() { Name = "Skip tour" }).ClickAsync(),
            candidate => candidate.Url.Contains("/api/v1/me/tour-completed", StringComparison.Ordinal) &&
                candidate.Request.Method == "POST");
        Assert.Equal(204, tourCompletedResponse.Status);

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
        ILocator acceptButton = recipient.Page
            .GetByRole(AriaRole.Button, new() { Name = $"Accept request @{requester.Username}" });
        await acceptButton.ClickAsync();
        // Callers navigate `recipient.Page` immediately after this returns; returning on the click
        // alone lets that navigation abort the in-flight accept POST, which the server then sees as
        // a cancelled request and answers 500 — so wait for the accept mutation to actually settle
        // (the pending-request row leaving the list). A genuine accept failure leaves the button in
        // place and fails here, which is the point.
        await acceptButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
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
        // the confirmation in `AccountSettingsPage`'s `onSuccess`), so this waits on a
        // language-neutral non-empty `role="status"` region rather than an English string. Both
        // the main scope and the non-empty filter are load-bearing: the app chrome's notification
        // bell carries its own status region in the banner (empty at first, announcing counts
        // later), and matching it resolves this wait before the locale save lands — the caller
        // then navigates away and cancels the in-flight PUT.
        await page.Locator("main [role='status']")
            .Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(".+") })
            .First.WaitForAsync();
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
    /// PHASE7_DESIGN.md §6: a fresh P-256 keypair per run — never a checked-in key, never the
    /// production keypair (§8, owner-provisioned). `LoggingPushSender` (`Push:Provider=log`) never
    /// reads these bytes at all — it only needs `Push:Vapid:PublicKey` to be non-empty so
    /// `GetPushConfig` reports `pushEnabled=true`/a real `vapidPublicKey` — but both keys are
    /// exported in the exact shape `Lib.Net.Http.WebPush`'s `VapidAuthentication` expects (an
    /// uncompressed X9.62 point for the public key, the raw 32-byte scalar for the private key,
    /// both base64url) so the same fixture output stays valid if a later slice ever flips the
    /// lane to `Push:Provider=webpush`.
    /// </summary>
    private static (string PublicKey, string PrivateKey) GenerateVapidKeyPair()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = ecdsa.ExportParameters(includePrivateParameters: true);

        byte[] x = parameters.Q.X!;
        byte[] y = parameters.Q.Y!;
        byte[] uncompressedPoint = new byte[1 + x.Length + y.Length];
        uncompressedPoint[0] = 0x04;
        Buffer.BlockCopy(x, 0, uncompressedPoint, 1, x.Length);
        Buffer.BlockCopy(y, 0, uncompressedPoint, 1 + x.Length, y.Length);

        return (Base64UrlEncode(uncompressedPoint), Base64UrlEncode(parameters.D!));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The push-drop analogue of <see cref="HarvestLinkAsync"/>: polls
    /// <see cref="_pushDropPath"/> for at least <paramref name="minimumCount"/> JSON file(s)
    /// written after <paramref name="since"/> (`LoggingPushSender`'s one-file-per-send output),
    /// returning their full contents for the caller's own `tag`/`url`/no-credential assertions.
    /// </summary>
    public async Task<string[]> WaitForPushDropFilesAsync(
        DateTime since,
        int minimumCount = 1,
        TimeSpan? timeout = null)
    {
        DateTime sinceWithSkewGuard = since - TimeSpan.FromSeconds(2);
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            string[] candidates = Directory.Exists(_pushDropPath)
                ? Directory.EnumerateFiles(_pushDropPath, "*.json")
                    .Where(path => File.GetLastWriteTimeUtc(path) >= sinceWithSkewGuard)
                    .ToArray()
                : [];

            if (candidates.Length >= minimumCount)
            {
                var contents = new string[candidates.Length];
                for (int i = 0; i < candidates.Length; i++)
                {
                    contents[i] = await File.ReadAllTextAsync(candidates[i]);
                }

                return contents;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Fewer than {minimumCount} push-drop file(s) appeared under '{_pushDropPath}' within the timeout.");
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
