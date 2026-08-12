using System.Diagnostics;
using System.Net;
using Hangfire;
using Hangfire.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PartyUp.Api.Hosting;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class RecurringJobRegistrationHostStartupTests
{
    // A host booted in a non-"Testing" environment (recurring-job registration is only skipped in
    // "Testing") whose configured Db connection is unreachable must still finish starting and serve
    // requests. Other hosted services (AdminSeeder, deletion sweep, email dispatch) are removed here
    // so this test isolates the one behavior it exists to prove; none of them are in this ticket's
    // scope.
    [Fact]
    public async Task HostStarts_AndServesRequests_WhenConfiguredDbIsUnreachable()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(webHost =>
            {
                webHost.UseEnvironment("Development");
                webHost.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Db"] =
                            "Host=127.0.0.1;Port=59999;Database=unreachable;Username=none;" +
                            "Password=none;Timeout=1;Command Timeout=1",
                        ["Database:MigrateOnStartup"] = "false",
                    }));
                webHost.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddHostedService<RecurringJobRegistrationService>();
                });
            });

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/auth/antiforgery",
            timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// Unit-style coverage for <see cref="RecurringJobRegistrationService"/>'s retry loop, exercised
/// directly against a throwing <see cref="IRecurringJobManager"/> fake rather than a full host boot
/// (that host-level proof is <see cref="RecurringJobRegistrationHostStartupTests"/>). No
/// <c>Category=Integration</c> trait: this needs no database or Docker.
/// </summary>
public sealed class RecurringJobRegistrationServiceRetryTests
{
    [Fact]
    public async Task StartAsync_ReturnsPromptly_EvenWhenTheJobManagerThrowsSynchronously()
    {
        (RecurringJobRegistrationService service, ThrowingRecurringJobManager _) = CreateService();

        Stopwatch stopwatch = Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 1000,
            $"StartAsync took {stopwatch.ElapsedMilliseconds}ms; registration attempts must not block host start.");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesAfterAFailedAttempt()
    {
        (RecurringJobRegistrationService service, ThrowingRecurringJobManager manager) =
            CreateService(retryDelays: [TimeSpan.FromMilliseconds(20)]);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => manager.AttemptCount >= 3, TimeSpan.FromSeconds(5));

        Assert.True(manager.AttemptCount >= 3, "expected the failed registration to be retried");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CancelsPromptly_WithoutWaitingOutTheRetryDelay()
    {
        (RecurringJobRegistrationService service, ThrowingRecurringJobManager manager) = CreateService();

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => manager.AttemptCount >= 1, TimeSpan.FromSeconds(5));

        Stopwatch stopwatch = Stopwatch.StartNew();
        await service.StopAsync(CancellationToken.None);
        stopwatch.Stop();

        // The shortest retry delay is 5s; a prompt stop proves stoppingToken cancellation propagates
        // through the Task.Delay wait rather than being ridden out.
        Assert.True(
            stopwatch.ElapsedMilliseconds < 4000,
            $"StopAsync took {stopwatch.ElapsedMilliseconds}ms; cancellation should not wait out the retry delay.");
    }

    /// <summary>
    /// PHASE5_DESIGN.md §7/§11: Phase 5 adds no Hangfire job and raises no notification event. Phase 6
    /// S3 adds the poll analogues (PHASE6_DESIGN.md §3) — the due sweep and the D-120
    /// deadline-approaching notice — bringing the registered set to four. Phase 7 S3 adds the push
    /// dispatch job and the send-log prune job (PHASE7_DESIGN.md §3), bringing it to six.
    /// AVATARS_DESIGN.md §3.4 adds the daily Steam avatar refresh, bringing it to seven.
    /// STEAM_CATALOG_DESIGN.md §1 adds the 6-hour catalog refresh, bringing it to eight. Asserted
    /// here, against the same real <see cref="RecurringJobRegistrationService"/> production
    /// registration path the retry tests above drive — no host, no environment name, no Hangfire
    /// storage — rather than from an integration test under the "Testing" host, which observes an
    /// empty registration set by construction (<c>Program.cs</c>, <c>PartyUpApiFactory.cs</c>,
    /// ARCHITECTURE.md:217) and so could never fail if a slice added a ninth job.
    /// </summary>
    [Fact]
    public async Task RegistersExactlyTheEightRecurringJobs_A066_D194()
    {
        var manager = new RecordingRecurringJobManager();
        ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IRecurringJobManager>(manager)
            .BuildServiceProvider();
        var service = new RecurringJobRegistrationService(
            new FakeServiceScopeFactory(provider),
            NullLogger<RecurringJobRegistrationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => manager.RegisteredJobIds.Count >= 8, TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(
            new HashSet<string>
            {
                "session-due-sweep",
                "session-reminder-due",
                "poll-due-sweep",
                "poll-deadline-approaching",
                "push-dispatch",
                "push-send-log-prune",
                "avatar-refresh",
                "steam-catalog-refresh",
            },
            manager.RegisteredJobIds);
    }

    private static (RecurringJobRegistrationService Service, ThrowingRecurringJobManager Manager) CreateService(
        TimeSpan[]? retryDelays = null)
    {
        var manager = new ThrowingRecurringJobManager();
        ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IRecurringJobManager>(manager)
            .BuildServiceProvider();
        var service = new RecurringJobRegistrationService(
            new FakeServiceScopeFactory(provider),
            NullLogger<RecurringJobRegistrationService>.Instance,
            retryDelays);
        return (service, manager);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ThrowingRecurringJobManager : IRecurringJobManager
    {
        private int _attemptCount;

        public int AttemptCount => _attemptCount;

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)
        {
            Interlocked.Increment(ref _attemptCount);
            throw new InvalidOperationException("Simulated Hangfire storage failure.");
        }

        public void Trigger(string recurringJobId) => throw new NotSupportedException();

        public void RemoveIfExists(string recurringJobId) => throw new NotSupportedException();
    }

    /// <summary>
    /// A recording sibling of <see cref="ThrowingRecurringJobManager"/>: captures every
    /// <c>recurringJobId</c> passed to <see cref="AddOrUpdate"/> instead of throwing, so
    /// <see cref="RegistersExactlyTheTwoPhase4RecurringJobs_A066"/> can assert the exact set the real
    /// production registration path names.
    /// </summary>
    private sealed class RecordingRecurringJobManager : IRecurringJobManager
    {
        private readonly HashSet<string> _registeredJobIds = [];

        public HashSet<string> RegisteredJobIds => _registeredJobIds;

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)
        {
            lock (_registeredJobIds)
            {
                _registeredJobIds.Add(recurringJobId);
            }
        }

        public void Trigger(string recurringJobId) => throw new NotSupportedException();

        public void RemoveIfExists(string recurringJobId) => throw new NotSupportedException();
    }

    private sealed class FakeServiceScopeFactory(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FakeServiceScope(provider);
    }

    private sealed class FakeServiceScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider => provider;

        public void Dispose()
        {
        }
    }
}
