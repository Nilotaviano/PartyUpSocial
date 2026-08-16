using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Jobs;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// S3's <see cref="SessionDueSweepJob"/> gate (PHASE4_DESIGN.md §4.1, §7.2, A-066): the bounded
/// due-sweep predicate finds every session whose end has passed, applies the same shared
/// <c>SessionTransitionUseCase</c> every request path uses, and does so idempotently, in its own
/// per-candidate transaction, surfacing an overlapping tick as a failed job rather than duplicated work.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionSweepTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RepeatedDueSweep_IsIdempotentAndUsesMaterializedStatus_D056_D164()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "sweep-idempotent-organizer@example.test");
        Session session = CreateSession(organizerId, minAttendance: 1, leadMinutes: 20, durationMinutes: 15);
        await SeedSessionAsync(session);

        _factory.Clock.Advance(Duration.FromMinutes(40));

        await RunSweepAsync();
        Session afterFirstSweep = await ReloadAsync(session.Id);
        Assert.Equal(SessionStatus.Completed, afterFirstSweep.Status);

        // A second sweep over the same, now-terminal session must be a pure no-op: the status column
        // is what the second pass reads and trusts (A-067), not a re-derivation from timestamps that
        // could flip it again or throw on an already-terminal aggregate.
        await RunSweepAsync();
        Session afterSecondSweep = await ReloadAsync(session.Id);
        Assert.Equal(SessionStatus.Completed, afterSecondSweep.Status);
    }

    [Fact]
    public async Task ConfirmedCompletesAndUnconfirmedExpiresAtEndTime_D056_D164()
    {
        UserId confirmedOrganizerId = await _factory.SeedUserAsync(email: "sweep-confirmed-organizer@example.test");
        UserId unconfirmedOrganizerId = await _factory.SeedUserAsync(email: "sweep-unconfirmed-organizer@example.test");

        // minAttendance 1: the organizer's own Going seat confirms it immediately.
        Session confirmed = CreateSession(confirmedOrganizerId, minAttendance: 1, leadMinutes: 20, durationMinutes: 15);
        // minAttendance 2: the organizer alone never reaches it, so it stays Unconfirmed.
        Session unconfirmed = CreateSession(unconfirmedOrganizerId, minAttendance: 2, leadMinutes: 20, durationMinutes: 15);
        Assert.Equal(SessionStatus.Confirmed, confirmed.Status);
        Assert.Equal(SessionStatus.Unconfirmed, unconfirmed.Status);

        await SeedSessionAsync(confirmed);
        await SeedSessionAsync(unconfirmed);

        _factory.Clock.Advance(Duration.FromMinutes(40));

        await RunSweepAsync();

        Assert.Equal(SessionStatus.Completed, (await ReloadAsync(confirmed.Id)).Status);
        Assert.Equal(SessionStatus.Expired, (await ReloadAsync(unconfirmed.Id)).Status);
    }

    /// <summary>
    /// §7.2: "each candidate gets its own transaction" — a session blocked behind an externally held
    /// parent lock must never hold up a different, already-due session's commit. Session A is more
    /// overdue than session B (so the sweep's <c>ORDER BY start_time, id</c> reaches A first); B's row
    /// is held externally for the whole first half of this test.
    /// </summary>
    [Fact]
    public async Task EachCandidateCommitsInItsOwnBoundedTransaction_A066()
    {
        UserId organizerA = await _factory.SeedUserAsync(email: "sweep-tx-a@example.test");
        UserId organizerB = await _factory.SeedUserAsync(email: "sweep-tx-b@example.test");

        Session sessionA = CreateSession(organizerA, minAttendance: 1, leadMinutes: 15, durationMinutes: 15);
        Session sessionB = CreateSession(organizerB, minAttendance: 1, leadMinutes: 20, durationMinutes: 15);
        await SeedSessionAsync(sessionA);
        await SeedSessionAsync(sessionB);

        _factory.Clock.Advance(Duration.FromMinutes(40));

        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM sessions WHERE id = {sessionB.Id.Value} FOR NO KEY UPDATE");

        await using AsyncServiceScope jobScope = _factory.Services.CreateAsyncScope();
        var job = jobScope.ServiceProvider.GetRequiredService<SessionDueSweepJob>();
        Task sweepTask = job.ExecuteAsync(default);

        Session? reloadedA = null;
        for (int attempt = 0; attempt < 50 && reloadedA?.Status != SessionStatus.Completed; attempt++)
        {
            await Task.Delay(100);
            reloadedA = await ReloadAsync(sessionA.Id);
        }

        Assert.Equal(SessionStatus.Completed, reloadedA?.Status);
        Assert.False(sweepTask.IsCompleted, "The sweep must still be blocked processing the externally locked B.");

        await holderTransaction.RollbackAsync();
        await sweepTask;

        Assert.Equal(SessionStatus.Completed, (await ReloadAsync(sessionB.Id)).Status);
    }

    /// <summary>
    /// The 1440-minute window is the exact duration cap, not a heuristic margin: a session whose
    /// duration is the maximum (1440) and whose start sits precisely at <c>now - 1440 minutes</c> —
    /// so its end lands exactly at <c>now</c> — must still be found. Anything looser than this exact
    /// bound would silently drop the longest-duration sessions right at the cap.
    /// </summary>
    [Fact]
    public async Task BoundedStartTimeWindowFindsEveryDueSessionAtTheDurationCap_D056()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "sweep-window-organizer@example.test");
        Session session = CreateSession(organizerId, minAttendance: 1, leadMinutes: 60, durationMinutes: 1440);
        await SeedSessionAsync(session);

        // now advances so that start_time (t0+60m) is exactly now-1440m, i.e. end_time == now.
        _factory.Clock.Advance(Duration.FromMinutes(1500));

        await RunSweepAsync();

        Assert.Equal(SessionStatus.Completed, (await ReloadAsync(session.Id)).Status);
    }

    /// <summary>
    /// A-066: both jobs carry the exact 55-second <c>DisableConcurrentExecution</c> contract, and the
    /// generic Hangfire mechanism it relies on genuinely throws when a resource is already held rather
    /// than silently letting a second worker in — that is what turns an overlapping tick into a failed
    /// job instead of a silent skip. Separately, running the real job concurrently against the same due
    /// session must never duplicate the transition: the aggregate's own idempotent due-transition
    /// function is what actually protects against double-processing under the shared parent lock.
    /// </summary>
    [Fact]
    public async Task OverlappingTickSurfacesAsAFailedJobAndDuplicatesNoWork_A066()
    {
        AssertDisableConcurrentExecutionContract(typeof(SessionDueSweepJob));
        AssertDisableConcurrentExecutionContract(typeof(SessionReminderDueJob));

        JobStorage storage = _factory.Services.GetRequiredService<JobStorage>();
        string resource = $"overlap-test-{Guid.NewGuid():N}";

        using Hangfire.Storage.IStorageConnection holder = storage.GetConnection();
        using (holder.AcquireDistributedLock(resource, TimeSpan.FromSeconds(30)))
        {
            using Hangfire.Storage.IStorageConnection contender = storage.GetConnection();
            Exception? thrown = await Record.ExceptionAsync(() =>
                Task.Run(() => contender.AcquireDistributedLock(resource, TimeSpan.FromSeconds(1))));

            Assert.IsAssignableFrom<Hangfire.Storage.DistributedLockTimeoutException>(thrown);
        }

        UserId organizerId = await _factory.SeedUserAsync(email: "sweep-overlap-organizer@example.test");
        Session session = CreateSession(organizerId, minAttendance: 2, leadMinutes: 20, durationMinutes: 15);
        await SeedSessionAsync(session);

        _factory.Clock.Advance(Duration.FromMinutes(40));

        await using AsyncServiceScope scopeA = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope scopeB = _factory.Services.CreateAsyncScope();
        SessionDueSweepJob jobA = scopeA.ServiceProvider.GetRequiredService<SessionDueSweepJob>();
        SessionDueSweepJob jobB = scopeB.ServiceProvider.GetRequiredService<SessionDueSweepJob>();

        await Task.WhenAll(jobA.ExecuteAsync(default), jobB.ExecuteAsync(default));

        Assert.Equal(SessionStatus.Expired, (await ReloadAsync(session.Id)).Status);
    }

    private static void AssertDisableConcurrentExecutionContract(Type jobType)
    {
        System.Reflection.MethodInfo method = jobType.GetMethod("ExecuteAsync")
            ?? throw new InvalidOperationException($"{jobType} has no ExecuteAsync method.");
        var attribute = (DisableConcurrentExecutionAttribute?)Attribute.GetCustomAttribute(
            method, typeof(DisableConcurrentExecutionAttribute));

        Assert.NotNull(attribute);
        Assert.Equal(55, attribute!.TimeoutSec);
    }

    private async Task SeedSessionAsync(Session session)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
    }

    private async Task RunSweepAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<SessionDueSweepJob>();
        await job.ExecuteAsync(default);
    }

    private async Task<Session> ReloadAsync(SessionId id)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Sessions.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private Session CreateSession(
        UserId organizerId,
        int minAttendance,
        int leadMinutes,
        int durationMinutes)
    {
        SessionDetails details = new(
            SessionTitle.Create("Sweep test session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(leadMinutes),
            Duration15.FromMinutes(durationMinutes),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance,
            maxCapacity: null,
            visibility: (true ? PartyUp.Domain.Kernel.ContentVisibility.Private : PartyUp.Domain.Kernel.ContentVisibility.Public));
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        _ = session.DequeueEvents();
        return session;
    }
}
