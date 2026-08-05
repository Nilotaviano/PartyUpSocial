using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Microsoft.Extensions.DependencyInjection;
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
        Session? reloaded;
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
        Assert.Equal(2, reloaded!.Participations.Count);
        Assert.Contains(reloaded.Participations, p => p.UserId == secondUserId);
    }

    [Fact]
    public async Task MultiRowLocksAreOrderedByTheDatabaseNotTheCaller_A068()
    {
        UserId userA = await _factory.SeedUserAsync(email: "order-a@example.test");
        UserId userB = await _factory.SeedUserAsync(email: "order-b@example.test");

        Task first = LockAndReleaseAsync([userA, userB]);
        Task second = LockAndReleaseAsync([userB, userA]);

        Task both = Task.WhenAll(first, second);
        Task winner = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.True(
            ReferenceEquals(winner, both),
            "Two callers locking the same two users in opposite orders must never deadlock: A-068 " +
            "requires the database's declared ORDER BY to decide acquisition order, not the caller's array.");

        // Observe any exception (e.g. a genuine deadlock) rather than swallowing it silently.
        await both;
    }

    private async Task LockAndReleaseAsync(IReadOnlyCollection<UserId> order)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISessionLockRepository>();

        await using var transaction = await context.Database.BeginTransactionAsync();
        await repository.LockUsersAsync(order, default);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await transaction.CommitAsync();
    }
}
