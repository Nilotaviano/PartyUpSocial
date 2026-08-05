using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// The A-068/A-069 lock gates (PHASE4_DESIGN.md §6.1 step 2/4): the session parent is locked before
/// any child row is read, and a multi-row lock is ordered by Postgres — never by the order the caller
/// happened to pass ids in.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionRepositoryLockTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ParentLockPrecedesEveryChildQuery_D084()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "lock-organizer@example.test");
        UserId secondUserId = await _factory.SeedUserAsync(email: "lock-second@example.test");

        SessionDetails details = new(
            SessionTitle.Create("Lock order test"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(2),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: true);
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        SessionId sessionId = session.Id;

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            var repository = seedScope.ServiceProvider.GetRequiredService<ISessionRepository>();
            repository.Add(session);
            await seedScope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync();
        }

        using SemaphoreSlim writerLockAcquired = new(0, 1);

        Task writer = Task.Run(async () =>
        {
            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM sessions WHERE id = {sessionId.Value} FOR NO KEY UPDATE");
            writerLockAcquired.Release();

            // Mutate a child row while still holding the uncommitted parent lock: a reader that races
            // ahead of the lock (reading children before it) would miss this row entirely.
            Session tracked = await context.Sessions
                .Include(s => s.Participations)
                .SingleAsync(s => s.Id == sessionId);
            tracked.Invite(secondUserId, _factory.Clock);
            await context.SaveChangesAsync();

            await Task.Delay(TimeSpan.FromMilliseconds(400));
            await transaction.CommitAsync();
        });

        await writerLockAcquired.WaitAsync();

        Stopwatch stopwatch = Stopwatch.StartNew();
        PartyUp.Application.Sessions.SessionForUpdate? reloaded;
        await using (AsyncServiceScope readerScope = _factory.Services.CreateAsyncScope())
        {
            var repository = readerScope.ServiceProvider.GetRequiredService<ISessionRepository>();
            reloaded = await repository.LoadForUpdateAsync(sessionId, default);
        }

        stopwatch.Stop();
        await writer;

        Assert.NotNull(reloaded);
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 300,
            $"LoadForUpdateAsync returned after only {stopwatch.ElapsedMilliseconds} ms; it must block on the " +
            "writer's parent lock rather than reading children ahead of it.");
        Assert.Equal(2, reloaded!.Session.Participations.Count);
        Assert.Contains(reloaded.Session.Participations, p => p.UserId == secondUserId);
    }

    /// <summary>
    /// Deterministic proof, not a timing-luck race (F5): A-068 declares <c>users</c> → <c>ORDER BY id</c>,
    /// so a correct single-statement <c>LockUsersAsync</c> always acquires the lower-id row first and
    /// only then blocks on a contended higher-id row — regardless of what order its caller's array held
    /// the ids in. This holds the higher-id row locked in one transaction, calls
    /// <see cref="ISessionLockRepository.LockUsersAsync"/> with ids in the array order
    /// <c>[higher, lower]</c> (the opposite of the declared DB order) from a second, and while that call
    /// is blocked, a third transaction attempts <c>FOR NO KEY UPDATE NOWAIT</c> on the lower-id row.
    /// A correct implementation has already locked the lower row before blocking, so the probe must
    /// fail with <c>55P03 lock_not_available</c>; a hypothetical per-row, caller-order implementation
    /// would instead block on the higher row first without ever touching the lower one, and the probe
    /// would wrongly succeed. That is the failure this test is built to catch.
    /// </summary>
    [Fact]
    public async Task MultiRowLocksAreOrderedByTheDatabaseNotTheCaller_A068()
    {
        UserId userA = await _factory.SeedUserAsync(email: "order-a@example.test");
        UserId userB = await _factory.SeedUserAsync(email: "order-b@example.test");

        // A-068's C#-side total order for diagnostics/assertions: big-endian byte comparison, which
        // reproduces Postgres `uuid` ordering; `Guid.CompareTo` does not (ARCHITECTURE.md A-068).
        int order = userA.Value.ToByteArray(bigEndian: true).AsSpan()
            .SequenceCompareTo(userB.Value.ToByteArray(bigEndian: true));
        (UserId lowerId, UserId higherId) = order < 0 ? (userA, userB) : (userB, userA);

        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM users WHERE id = {higherId.Value} FOR NO KEY UPDATE");

        await using AsyncServiceScope callerScope = _factory.Services.CreateAsyncScope();
        AppDbContext callerContext = callerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = callerScope.ServiceProvider.GetRequiredService<ISessionLockRepository>();
        await using var callerTransaction = await callerContext.Database.BeginTransactionAsync();

        Task callerLock = repository.LockUsersAsync([higherId, lowerId], default);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(
            callerLock.IsCompleted,
            "The multi-row lock must still be blocked on the contended higher-id row.");

        await using (AsyncServiceScope probeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext probeContext = probeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var probeTransaction = await probeContext.Database.BeginTransactionAsync();

            Exception? thrown = await Record.ExceptionAsync(() =>
                probeContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM users WHERE id = {lowerId.Value} FOR NO KEY UPDATE NOWAIT"));

            Assert.True(
                thrown is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable },
                "The lower-id row must already be locked by the blocked caller — A-068 orders by id, so " +
                "the lower row is acquired before the caller blocks on the higher one. A caller-order " +
                "implementation would leave it unlocked while blocked on the higher row instead.");

            await probeTransaction.RollbackAsync();
        }

        await holderTransaction.RollbackAsync();
        await callerLock;
        await callerTransaction.CommitAsync();
    }
}
