using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using PartyUp.Application.Availability;
using PartyUp.Application.Profile;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md P5-LK-01: the owner's <c>users</c> row is the serialization point for every
/// availability write (A-075). Proved through the real S3 command surface, not raw SQL, mirroring
/// <c>Sessions/SessionLockingTests.cs</c>'s precedent for the session-parent lock.
///
/// Round-1 verification (findings F1/F2) required every race here to be a <em>genuine, forced</em>
/// interleaving rather than two un-awaited tasks racing on their own schedule: the "winner" side of
/// each race runs the real use case inside a transaction this test opens and holds open itself — a
/// real barrier — so the "loser" side is provably blocked on the shared row, not merely likely to be.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityLockingTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb["America/Sao_Paulo"];

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// A-075: <c>LockBlocksBySourceSeriesAsync</c> issues one <c>SELECT … ORDER BY id FOR NO KEY
    /// UPDATE</c> over every materialized block of a series. With the higher-id block externally
    /// held, the statement can only be blocked on it after already locking the lower-id row in the
    /// declared order — proved by a failing <c>NOWAIT</c> probe on the lower row while the delete is
    /// still pending, the same deterministic shape as
    /// <c>SessionRepositoryLockTests.MultiRowLocksAreOrderedByTheDatabaseNotTheCaller_A068</c>.
    /// </summary>
    [Fact]
    public async Task OwnerSeriesBlockAndSkipLocksAreOrderedByTheDatabase_A075()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"lock-order-{Guid.NewGuid():N}@example.test");
        Guid seriesId;
        Guid blockAId;
        Guid blockBId;

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AvailabilitySeries series = AvailabilitySeries.Create(
                ownerId,
                WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60)),
                Zone,
                new LocalDate(2026, 9, 1));
            seriesId = series.Id.Value;

            LocalDate dateA = new(2026, 9, 8);
            LocalDate dateB = new(2026, 9, 15);
            Instant startA = dateA.AtStartOfDayInZone(Zone).ToInstant();
            Instant startB = dateB.AtStartOfDayInZone(Zone).ToInstant();
            AvailabilityBlock blockA = AvailabilityBlock.Materialize(
                series, dateA, new TimeRange(startA, startA + Duration.FromHours(1)));
            AvailabilityBlock blockB = AvailabilityBlock.Materialize(
                series, dateB, new TimeRange(startB, startB + Duration.FromHours(1)));
            blockAId = blockA.Id.Value;
            blockBId = blockB.Id.Value;

            context.AvailabilitySeries.Add(series);
            context.AvailabilityBlocks.Add(blockA);
            context.AvailabilityBlocks.Add(blockB);
            await context.SaveChangesAsync();
        }

        // Postgres orders a uuid column by its RFC 4122 byte layout, which .NET's Guid.CompareTo does
        // not match (its first three fields are little-endian in memory) — compare the big-endian
        // byte sequence instead, the same idiom SessionLockingTests uses for user ids.
        int order = blockAId.ToByteArray(bigEndian: true).AsSpan()
            .SequenceCompareTo(blockBId.ToByteArray(bigEndian: true));
        (Guid lowerId, Guid higherId) = order < 0 ? (blockAId, blockBId) : (blockBId, blockAId);

        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM availability_blocks WHERE id = {higherId} FOR NO KEY UPDATE");

        await using AsyncServiceScope commandScope = _factory.Services.CreateAsyncScope();
        var deleteUseCase = commandScope.ServiceProvider.GetRequiredService<DeleteAvailabilitySeriesUseCase>();
        Task<bool> commandTask = deleteUseCase.ExecuteAsync(ownerId, AvailabilitySeriesId.From(seriesId), default);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(commandTask.IsCompleted, "The delete must still be blocked on the externally held higher-id block.");

        await using (AsyncServiceScope probeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext probeContext = probeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var probeTransaction = await probeContext.Database.BeginTransactionAsync();

            Exception? thrown = await Record.ExceptionAsync(() =>
                probeContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM availability_blocks WHERE id = {lowerId} FOR NO KEY UPDATE NOWAIT"));

            Assert.True(
                thrown is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable },
                "The lower-id block must already be locked by the blocked delete — A-075 orders the " +
                "statement by id, so the lower row is acquired before the caller ever blocks on the higher one.");

            await probeTransaction.RollbackAsync();
        }

        await holderTransaction.RollbackAsync();
        bool result = await commandTask;
        Assert.True(result);
    }

    /// <summary>
    /// P5-LK-01: the owner's <c>users</c> row self-conflicts (<c>FOR NO KEY UPDATE</c> against itself),
    /// so two concurrent <c>thisAndFuture</c> splits of the same occurrence are strictly ordered.
    /// Round-1 fix (F2c): the winner runs a real <see cref="EditAvailabilityOccurrenceUseCase"/> call
    /// inside a transaction this test holds open — a genuine barrier — so the loser's attempt is
    /// provably blocked on that exact row before it can even re-read the series, not merely likely to
    /// lose a schedule race. The loser's forced-fresh read (only reachable after the winner commits)
    /// sees the already-truncated original and fails <c>EnsureOccurrenceDate</c> — exactly one
    /// successor ever results, never two, and never an orphaned pair.
    /// </summary>
    [Fact]
    public async Task ConcurrentThisAndFutureSplitsProduceOneSuccessor_D143()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"lock-split-{Guid.NewGuid():N}@example.test");
        Guid seriesId = await SeedSeriesAsync(ownerId);
        LocalDate splitDate = new(2026, 9, 15);

        WeeklyPattern patternA = WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(20, 0), Duration15.FromMinutes(90));
        WeeklyPattern patternB = WeeklyPattern.Create(IsoDayOfWeek.Wednesday, new LocalTime(9, 0), Duration15.FromMinutes(45));

        await using AsyncServiceScope winnerScope = _factory.Services.CreateAsyncScope();
        AppDbContext winnerContext = winnerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var winnerTransaction = await winnerContext.Database.BeginTransactionAsync();
        var winnerUseCase = winnerScope.ServiceProvider.GetRequiredService<EditAvailabilityOccurrenceUseCase>();

        // Runs to completion against the ambient (still-open, uncommitted) transaction: the nested
        // branch of IUnitOfWork.ExecuteAsync takes over, so the users/series row locks this call
        // acquires stay held until we explicitly commit below.
        await winnerUseCase.ExecuteAsync(
            ownerId, AvailabilitySeriesId.From(seriesId), splitDate, OccurrenceEditScope.ThisAndFuture, null, patternA, default);

        await using AsyncServiceScope loserScope = _factory.Services.CreateAsyncScope();
        var loserUseCase = loserScope.ServiceProvider.GetRequiredService<EditAvailabilityOccurrenceUseCase>();
        Task<OccurrenceEditOutcome?> loserTask = CatchNotFoundAsync(() => loserUseCase.ExecuteAsync(
            ownerId, AvailabilitySeriesId.From(seriesId), splitDate, OccurrenceEditScope.ThisAndFuture, null, patternB, default));

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(
            loserTask.IsCompleted,
            "The second split must block on the winner's held users row (P5-LK-01) before it can even re-read the series.");

        await winnerContext.SaveChangesAsync();
        await winnerTransaction.CommitAsync();

        OccurrenceEditOutcome? loserOutcome = await loserTask;
        Assert.Null(loserOutcome); // the loser's fresh read sees the already-truncated original.

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        int seriesCount = await verify.AvailabilitySeries.AsNoTracking().CountAsync(s => s.OwnerId == ownerId);
        Assert.Equal(2, seriesCount); // the truncated original plus exactly one successor.
    }

    /// <summary>
    /// P5-LK-01: a <c>thisAndFuture</c> split racing a delete-one on the same date serializes on the
    /// owner lock — a row lock on <c>availability_occurrence_skips</c> could not achieve this, since
    /// <c>FOR NO KEY UPDATE</c> never blocks an <c>INSERT</c>. Round-1 fix chain (F2a+F2b): delete-one
    /// takes no series-row lock at all (<see cref="DeleteAvailabilityOccurrenceUseCase"/> reads the
    /// series with the non-locking <c>FindSeriesAsync</c>, since it never mutates the series row) — so
    /// the owner's <c>users</c> row is the <em>only</em> lock this pair shares, which is exactly what
    /// this theory discriminates: with no shared series lock, the two orderings below still serialize
    /// on users alone. Both orderings are forced with a real held transaction (the same barrier
    /// idiom as the split-vs-split race above), and whichever wins, no skip survives pointing into
    /// the split's dropped tail.
    /// </summary>
    [Theory]
    [InlineData(true)] // the split commits first; delete-one is the loser.
    [InlineData(false)] // delete-one commits first; the split is the loser.
    public async Task SplitAndDeleteOneOnTheSameDateSerializeOnTheOwnerLock_D143(bool splitWins)
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"lock-split-delete-{Guid.NewGuid():N}@example.test");
        Guid seriesId = await SeedSeriesAsync(ownerId);
        LocalDate splitDate = new(2026, 9, 15);
        WeeklyPattern replacement = WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(20, 0), Duration15.FromMinutes(90));

        await using AsyncServiceScope winnerScope = _factory.Services.CreateAsyncScope();
        AppDbContext winnerContext = winnerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var winnerTransaction = await winnerContext.Database.BeginTransactionAsync();
        await using AsyncServiceScope loserScope = _factory.Services.CreateAsyncScope();

        Task loserTask;
        Task<OccurrenceEditOutcome?>? loserSplitTask = null;
        Task<bool?>? loserDeleteTask = null;

        if (splitWins)
        {
            var winnerUseCase = winnerScope.ServiceProvider.GetRequiredService<EditAvailabilityOccurrenceUseCase>();
            await winnerUseCase.ExecuteAsync(
                ownerId, AvailabilitySeriesId.From(seriesId), splitDate, OccurrenceEditScope.ThisAndFuture, null, replacement, default);

            var loserUseCase = loserScope.ServiceProvider.GetRequiredService<DeleteAvailabilityOccurrenceUseCase>();
            loserDeleteTask = CatchNotFoundAsync(() => loserUseCase.ExecuteAsync(
                ownerId, AvailabilitySeriesId.From(seriesId), splitDate, default));
            loserTask = loserDeleteTask;
        }
        else
        {
            var winnerUseCase = winnerScope.ServiceProvider.GetRequiredService<DeleteAvailabilityOccurrenceUseCase>();
            await winnerUseCase.ExecuteAsync(ownerId, AvailabilitySeriesId.From(seriesId), splitDate, default);

            var loserUseCase = loserScope.ServiceProvider.GetRequiredService<EditAvailabilityOccurrenceUseCase>();
            loserSplitTask = CatchNotFoundAsync(() => loserUseCase.ExecuteAsync(
                ownerId, AvailabilitySeriesId.From(seriesId), splitDate, OccurrenceEditScope.ThisAndFuture, null, replacement, default));
            loserTask = loserSplitTask;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(
            loserTask.IsCompleted,
            "The loser must block on the winner's held users row before its skip INSERT (or split re-read) can run.");

        await winnerContext.SaveChangesAsync();
        await winnerTransaction.CommitAsync();

        await loserTask;

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        AvailabilitySeriesId typedSeriesId = AvailabilitySeriesId.From(seriesId);
        int orphanedSkips = await verify.AvailabilityOccurrenceSkips.AsNoTracking()
            .CountAsync(s => s.SeriesId == typedSeriesId && s.OccurrenceDate == splitDate);
        Assert.Equal(0, orphanedSkips);

        if (splitWins)
        {
            // The delete-one loser's forced-fresh (lockless) read finds the occurrence already
            // outside the truncated original's range.
            Assert.Null(await loserDeleteTask!);
        }
        else
        {
            // The split loser succeeds; its own RemoveSkipsFrom(splitDate) drops the skip the
            // delete-one winner had just committed, so no orphan survives either way.
            Assert.NotNull(await loserSplitTask!);
        }
    }

    /// <summary>
    /// A-081: the profile path's <c>FOR UPDATE</c> (<c>UserRepository.FindForUpdateAsync</c>) and an
    /// availability write's <c>FOR NO KEY UPDATE</c> conflict on the same users row (the residual
    /// A-069 deadlock class), so a real <see cref="ChangeTimeZoneUseCase"/> zone migration and a real
    /// <see cref="EditAvailabilityBlockUseCase"/> edit for the same owner serialize rather than
    /// corrupt each other. Round-1 fix (F1): both use cases are actually invoked — the prior version
    /// only ever ran the block edit against a raw-SQL-simulated <c>FOR NO KEY UPDATE</c> holder and
    /// never called <c>ChangeTimeZoneUseCase</c> at all. The theory forces each legal ordering once
    /// with a real held transaction; the block's absolute instants read the same either way.
    /// </summary>
    [Theory]
    [InlineData(true)] // the zone migration commits first; the block edit is the loser.
    [InlineData(false)] // the block edit commits first; the zone migration is the loser.
    public async Task ZoneMigrationAndBlockEditSerializeOnTheOwnerRow_D061(bool zoneFirst)
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"lock-zone-{Guid.NewGuid():N}@example.test");
        DateTimeZone oldZone = DateTimeZoneProviders.Tzdb["America/Fortaleza"];
        DateTimeZone newZone = DateTimeZoneProviders.Tzdb["Europe/Lisbon"];
        Instant blockStart = Instant.FromUtc(2026, 9, 20, 10, 0);
        Instant blockEnd = blockStart + Duration.FromMinutes(30);
        Guid blockId;
        Guid seriesId;

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AvailabilityBlock block = AvailabilityBlock.Create(ownerId, new TimeRange(blockStart, blockEnd));
            blockId = block.Id.Value;
            AvailabilitySeries series = AvailabilitySeries.Create(
                ownerId,
                WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60)),
                oldZone,
                new LocalDate(2026, 9, 1));
            seriesId = series.Id.Value;
            context.AvailabilityBlocks.Add(block);
            context.AvailabilitySeries.Add(series);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope winnerScope = _factory.Services.CreateAsyncScope();
        AppDbContext winnerContext = winnerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var winnerTransaction = await winnerContext.Database.BeginTransactionAsync();
        await using AsyncServiceScope loserScope = _factory.Services.CreateAsyncScope();

        Instant newStart = blockStart + Duration.FromHours(3);
        Task loserTask;
        Task<ProfileMutationResult>? loserZoneTask = null;
        Task<AvailabilityBlock>? loserBlockTask = null;

        if (zoneFirst)
        {
            var winnerUseCase = winnerScope.ServiceProvider.GetRequiredService<ChangeTimeZoneUseCase>();
            ProfileMutationResult winnerResult = await winnerUseCase.ExecuteAsync(ownerId, newZone.Id, default);
            Assert.True(winnerResult.Succeeded);

            var loserUseCase = loserScope.ServiceProvider.GetRequiredService<EditAvailabilityBlockUseCase>();
            loserBlockTask = loserUseCase.ExecuteAsync(
                ownerId,
                AvailabilityBlockId.From(blockId),
                new TimeRange(newStart, newStart + Duration.FromMinutes(30)),
                default);
            loserTask = loserBlockTask;
        }
        else
        {
            var winnerUseCase = winnerScope.ServiceProvider.GetRequiredService<EditAvailabilityBlockUseCase>();
            AvailabilityBlock winnerBlock = await winnerUseCase.ExecuteAsync(
                ownerId,
                AvailabilityBlockId.From(blockId),
                new TimeRange(newStart, newStart + Duration.FromMinutes(30)),
                default);
            Assert.Equal(newStart, winnerBlock.Start);

            var loserUseCase = loserScope.ServiceProvider.GetRequiredService<ChangeTimeZoneUseCase>();
            loserZoneTask = loserUseCase.ExecuteAsync(ownerId, newZone.Id, default);
            loserTask = loserZoneTask;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(
            loserTask.IsCompleted,
            "The loser must block on the winner's held users row — A-081's FOR UPDATE/FOR NO KEY " +
            "UPDATE mixed-mode conflict, executed here, not narrated.");

        await winnerContext.SaveChangesAsync();
        await winnerTransaction.CommitAsync();

        await loserTask;

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        AvailabilityBlockId typedBlockId = AvailabilityBlockId.From(blockId);
        AvailabilitySeriesId typedSeriesId = AvailabilitySeriesId.From(seriesId);

        AvailabilityBlock reloadedBlock = await verify.AvailabilityBlocks.AsNoTracking()
            .SingleAsync(b => b.Id == typedBlockId);
        Assert.Equal(newStart, reloadedBlock.Start); // untouched by the zone migration, either order.
        Assert.Equal(newStart + Duration.FromMinutes(30), reloadedBlock.End);

        AvailabilitySeries reloadedSeries = await verify.AvailabilitySeries.AsNoTracking()
            .SingleAsync(s => s.Id == typedSeriesId);
        Assert.Equal(newZone.Id, reloadedSeries.ZoneId);

        User reloadedUser = await verify.DomainUsers.AsNoTracking().SingleAsync(u => u.Id == ownerId);
        Assert.Equal(newZone.Id, reloadedUser.TimeZone!.Id);

        if (zoneFirst)
        {
            Assert.Equal(newStart, (await loserBlockTask!).Start);
        }
        else
        {
            Assert.True((await loserZoneTask!).Succeeded);
        }
    }

    private async Task<Guid> SeedSeriesAsync(UserId ownerId)
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        AvailabilitySeries series = AvailabilitySeries.Create(
            ownerId,
            WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60)),
            Zone,
            new LocalDate(2026, 9, 1));
        context.AvailabilitySeries.Add(series);
        await context.SaveChangesAsync();
        return series.Id.Value;
    }

    private static async Task<OccurrenceEditOutcome?> CatchNotFoundAsync(Func<Task<OccurrenceEditOutcome>> action)
    {
        try
        {
            return await action();
        }
        catch (DomainException)
        {
            return null;
        }
    }

    private static async Task<bool?> CatchNotFoundAsync(Func<Task<bool>> action)
    {
        try
        {
            return await action();
        }
        catch (DomainException)
        {
            return null;
        }
    }
}
